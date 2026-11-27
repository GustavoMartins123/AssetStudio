using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    public sealed partial class SemanticGraphService
    {
        // --- Animation Relations ---

        private static void AddAnimatorAvatarMeshRelations(
            SerializedFile sourceFile,
            Animator animator,
            List<SkinnedMeshSemanticBinding> skinnedMeshBindings,
            List<RendererMeshSemanticBinding> meshRendererBindings,
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

            if (!string.IsNullOrEmpty(controllerId) && animator.m_Controller != null && !animator.m_Controller.IsNull)
            {
                AddAssetEdge(
                    relations,
                    animator,
                    "AnimatorController",
                    "m_Controller",
                    0,
                    controllerId,
                    animator.m_Controller.m_FileID,
                    animator.m_Controller.m_PathID);
            }

            var meshMatches = FindRendererMeshesForAnimator(sourceFile, animatorGameObject, skinnedMeshBindings, meshRendererBindings, animatorCount).ToList();
            var groupId = AddAnimatorModelGroupRelations(
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
            var primaryMeshId = string.Empty;
            var seenMeshes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in meshMatches)
            {
                if (string.IsNullOrEmpty(binding.MeshId) || !seenMeshes.Add(binding.MeshId))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(primaryMeshId))
                {
                    primaryMeshId = binding.MeshId;
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
                    animatorId,
                    avatarId,
                    avatarFileId,
                    avatarPathId,
                    primaryMeshId,
                    groupId,
                    depth: 0);
            }

            // Emit exact Animator-relative Transform path hashes for Avatar-less (Generic) rigs,
            // including nested Animators (e.g. facial rigs) whose bones belong to a sibling SkinnedMeshRenderer.
            if (string.IsNullOrEmpty(avatarId) && animatorGameObject != null && skinnedMeshBindings.Count > 0)
            {
                AddAnimatorTransformRigBoneHashes(
                    sourceFile,
                    animator,
                    animatorId,
                    animatorGameObject,
                    groupId,
                    primaryMeshId,
                    skinnedMeshBindings,
                    relations);
            }
        }

        private static string AddAnimatorModelGroupRelations(
            SerializedFile sourceFile,
            SemanticAssetRelations relations,
            Animator animator,
            string animatorId,
            GameObject? animatorGameObject,
            string rootGameObjectId,
            string avatarId,
            string controllerId,
            IReadOnlyList<RendererMeshSemanticBinding> meshMatches)
        {
            if (meshMatches.Count == 0)
            {
                return string.Empty;
            }

            var groupId = BuildAnimatorModelGroupId(sourceFile, animator, animatorId, rootGameObjectId, avatarId, controllerId);
            if (string.IsNullOrEmpty(groupId))
            {
                return string.Empty;
            }

            var groupName = !string.IsNullOrWhiteSpace(animatorGameObject?.m_Name)
                ? animatorGameObject!.m_Name
                : "Animator Model";
            var activeMatches = meshMatches
                .Where(match => string.IsNullOrEmpty(match.ConfidenceReason)
                    || !match.ConfidenceReason.Contains("(inactive)", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var confidenceSource = activeMatches.Count > 0 ? activeMatches : meshMatches;
            var confidence = confidenceSource.Min(match => match.Confidence);
            var confidenceReason = confidenceSource.All(match => match.ConfidenceReason == confidenceSource[0].ConfidenceReason)
                ? confidenceSource[0].ConfidenceReason
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
            foreach (var binding in meshMatches)
            {
                var rendererId = !string.IsNullOrEmpty(binding.RendererId)
                    ? binding.RendererId
                    : GetSemanticAssetId(binding.Renderer);
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
                    string.IsNullOrEmpty(binding.RendererType) ? "SkinnedMeshRenderer" : binding.RendererType,
                    gameObjectId,
                    binding.GameObject?.m_Name ?? string.Empty,
                    slotIndex,
                    BuildLocalToGroupRootMatrix(sourceFile, binding.GameObject, rootGameObjectId),
                    binding.Confidence,
                    binding.ConfidenceReason));
                slotIndex++;
            }

            return groupId;
        }

        public static string BuildAnimatorModelGroupId(
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

        private static IEnumerable<RendererMeshSemanticBinding> FindRendererMeshesForAnimator(
            SerializedFile sourceFile,
            GameObject? animatorGameObject,
            List<SkinnedMeshSemanticBinding> skinnedMeshBindings,
            List<RendererMeshSemanticBinding> meshRendererBindings,
            int animatorCount)
        {
            if (skinnedMeshBindings.Count == 0 && meshRendererBindings.Count == 0)
            {
                yield break;
            }

            if (animatorGameObject != null)
            {
                var matchedAnySkinned = false;
                foreach (var binding in skinnedMeshBindings)
                {
                    if (binding.GameObject != null
                        && IsSameOrDescendantGameObject(sourceFile, binding.GameObject, animatorGameObject))
                    {
                        matchedAnySkinned = true;
                        var isActive = IsRendererActiveInHierarchy(sourceFile, binding.Renderer, binding.GameObject, animatorGameObject);
                        yield return new RendererMeshSemanticBinding(
                            binding.Renderer,
                            GetSemanticAssetId(binding.Renderer),
                            "SkinnedMeshRenderer",
                            binding.Mesh,
                            binding.MeshId,
                            binding.GameObject,
                            binding.MeshFileId,
                            binding.MeshPathId,
                            isActive ? 100 : 40,
                            isActive ? "Animator hierarchy descendant" : "Animator hierarchy descendant (inactive)");
                    }
                }

                if (matchedAnySkinned)
                {
                    yield break;
                }

                var matchedAnyStatic = false;
                foreach (var binding in meshRendererBindings)
                {
                    if (binding.GameObject != null
                        && IsSameOrDescendantGameObject(sourceFile, binding.GameObject, animatorGameObject))
                    {
                        matchedAnyStatic = true;
                        var isActive = IsRendererActiveInHierarchy(sourceFile, binding.Renderer, binding.GameObject, animatorGameObject);
                        yield return new RendererMeshSemanticBinding(
                            binding.Renderer,
                            binding.RendererId,
                            "MeshRenderer",
                            binding.Mesh,
                            binding.MeshId,
                            binding.GameObject,
                            binding.MeshFileId,
                            binding.MeshPathId,
                            isActive ? 80 : 35,
                            isActive ? "Animator MeshRenderer descendant" : "Animator MeshRenderer descendant (inactive)");
                    }
                }

                if (matchedAnyStatic)
                {
                    yield break;
                }
            }

            if (animatorCount == 1)
            {
                if (skinnedMeshBindings.Count > 0)
                {
                    foreach (var binding in skinnedMeshBindings)
                    {
                        var isActive = IsRendererActiveInHierarchy(sourceFile, binding.Renderer, binding.GameObject, animatorGameObject);
                        yield return new RendererMeshSemanticBinding(
                            binding.Renderer,
                            GetSemanticAssetId(binding.Renderer),
                            "SkinnedMeshRenderer",
                            binding.Mesh,
                            binding.MeshId,
                            binding.GameObject,
                            binding.MeshFileId,
                            binding.MeshPathId,
                            isActive ? 20 : 10,
                            isActive ? "Single animator file fallback" : "Single animator file fallback (inactive)");
                    }

                    yield break;
                }

                foreach (var binding in meshRendererBindings)
                {
                    var isActive = IsRendererActiveInHierarchy(sourceFile, binding.Renderer, binding.GameObject, animatorGameObject);
                    yield return new RendererMeshSemanticBinding(
                        binding.Renderer,
                        binding.RendererId,
                        "MeshRenderer",
                        binding.Mesh,
                        binding.MeshId,
                        binding.GameObject,
                        binding.MeshFileId,
                        binding.MeshPathId,
                        isActive ? 15 : 8,
                        isActive ? "Single animator MeshRenderer file fallback" : "Single animator MeshRenderer file fallback (inactive)");
                }
            }
        }

        public static bool IsRendererActiveInHierarchy(
            SerializedFile sourceFile,
            Renderer? renderer,
            GameObject? gameObject,
            GameObject? stopAtAncestor = null)
        {
            return (renderer == null || renderer.m_Enabled)
                && IsGameObjectActiveInHierarchy(sourceFile, gameObject, stopAtAncestor);
        }

        public static bool IsGameObjectActiveInHierarchy(
            SerializedFile sourceFile,
            GameObject? gameObject,
            GameObject? stopAtAncestor = null)
        {
            if (gameObject == null)
            {
                return true;
            }

            var visited = new HashSet<long>();
            GameObject? current = gameObject;
            var stopPathId = stopAtAncestor?.m_PathID;
            for (var depth = 0; current != null && depth < 256; depth++)
            {
                if (!visited.Add(current.m_PathID))
                {
                    break;
                }

                if (!current.m_IsActive)
                {
                    return false;
                }

                if (stopPathId.HasValue && current.m_PathID == stopPathId.Value)
                {
                    break;
                }

                var currentFile = current.assetsFile ?? sourceFile;
                var transform = ResolveTransformForGameObjectBackground(currentFile, current);
                if (transform?.m_Father == null || transform.m_Father.IsNull)
                {
                    break;
                }

                var parentTransform = ResolveTransformBackground(currentFile, transform.m_Father);
                if (parentTransform == null)
                {
                    break;
                }

                current = ResolveGameObjectBackground(parentTransform.assetsFile ?? currentFile, parentTransform.m_GameObject);
            }

            return true;
        }

        private static void AddAnimatorTransformRigBoneHashes(
            SerializedFile sourceFile,
            Animator animator,
            string animatorId,
            GameObject animatorGameObject,
            string animatorGroupId,
            string primaryMeshId,
            List<SkinnedMeshSemanticBinding> skinnedMeshBindings,
            SemanticAssetRelations relations)
        {
            var animatorTransform = ResolveTransformForGameObjectBackground(sourceFile, animatorGameObject);
            if (animatorTransform == null)
            {
                return;
            }

            var animatorTransformPathId = animatorTransform.m_PathID;
            var relativeHashes = new HashSet<uint>();
            var resolvedMeshId = primaryMeshId;
            var resolvedGroupId = animatorGroupId;
            var pathSegments = new List<string>(16);
            var visited = new HashSet<long>();

            // Collect exact Animator-relative Transform path hashes for every bone (and ancestor transform
            // beneath the Animator) across all SkinnedMeshRenderers whose bones descend from this Animator.
            for (var bIdx = 0; bIdx < skinnedMeshBindings.Count; bIdx++)
            {
                var binding = skinnedMeshBindings[bIdx];
                var bones = binding.Renderer?.m_Bones;
                if (bones == null || bones.Length == 0)
                {
                    continue;
                }

                var matchedAnyBoneUnderAnimator = false;
                for (var i = 0; i < bones.Length; i++)
                {
                    var boneTransform = ResolveTransformBackground(sourceFile, bones[i]);
                    if (boneTransform == null)
                    {
                        continue;
                    }

                    if (!TryCollectRelativePathsToAnimator(
                        sourceFile,
                        boneTransform,
                        animatorTransformPathId,
                        pathSegments,
                        visited,
                        relativeHashes))
                    {
                        continue;
                    }

                    matchedAnyBoneUnderAnimator = true;
                }

                if (binding.Renderer?.m_RootBone != null && !binding.Renderer.m_RootBone.IsNull)
                {
                    var rootBoneTransform = ResolveTransformBackground(sourceFile, binding.Renderer.m_RootBone);
                    if (rootBoneTransform != null)
                    {
                        TryCollectRelativePathsToAnimator(
                            sourceFile,
                            rootBoneTransform,
                            animatorTransformPathId,
                            pathSegments,
                            visited,
                            relativeHashes);
                    }
                }

                if (matchedAnyBoneUnderAnimator && string.IsNullOrEmpty(resolvedMeshId) && !string.IsNullOrEmpty(binding.MeshId))
                {
                    resolvedMeshId = binding.MeshId;
                }
            }

            if (relativeHashes.Count == 0 || string.IsNullOrEmpty(resolvedMeshId))
            {
                return;
            }

            // If this is a nested Animator (e.g. a facial rig under a character body) that had no direct
            // descendant renderers of its own, associate it with the enclosing ModelGroup containing resolvedMeshId.
            if (string.IsNullOrEmpty(resolvedGroupId))
            {
                resolvedGroupId = relations.ModelGroupMeshes
                    .FirstOrDefault(m => string.Equals(m.MeshAssetId, resolvedMeshId, StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(m.GroupId))
                    ?.GroupId ?? string.Empty;
            }

            var selectedHashes = SelectDeterministicBoneHashProbes(relativeHashes, MaxRigBoneHashProbes);
            for (var i = 0; i < selectedHashes.Length; i++)
            {
                relations.RigBoneHashes.Add(new SemanticRigBoneHashEntry(
                    string.Empty,
                    animatorId,
                    resolvedMeshId,
                    resolvedGroupId,
                    selectedHashes[i]));
            }
        }

        private static bool TryCollectRelativePathsToAnimator(
            SerializedFile sourceFile,
            Transform leafTransform,
            long animatorTransformPathId,
            List<string> pathSegments,
            HashSet<long> visited,
            HashSet<uint> outputHashes)
        {
            pathSegments.Clear();
            visited.Clear();
            Transform? current = leafTransform;
            var reachedAnimator = false;

            for (var depth = 0; current != null && depth < 64; depth++)
            {
                if (!visited.Add(current.m_PathID))
                {
                    break;
                }

                if (current.m_PathID == animatorTransformPathId)
                {
                    reachedAnimator = true;
                    break;
                }

                var currentFile = current.assetsFile ?? sourceFile;
                var go = ResolveGameObjectBackground(currentFile, current.m_GameObject);
                pathSegments.Add(go?.m_Name ?? string.Empty);

                if (current.m_Father == null || current.m_Father.IsNull)
                {
                    break;
                }

                current = ResolveTransformBackground(currentFile, current.m_Father);
            }

            if (!reachedAnimator || pathSegments.Count == 0)
            {
                return false;
            }

            // Emit the Animator-relative path for this bone AND every intermediate ancestor transform
            // beneath the Animator (e.g. SklRootNode, SklRootNode/SK_Skeleton, ..., bone).
            pathSegments.Reverse();
            var builder = new System.Text.StringBuilder(128);
            for (var i = 0; i < pathSegments.Count; i++)
            {
                var segment = pathSegments[i];
                if (string.IsNullOrEmpty(segment))
                {
                    return true;
                }

                if (builder.Length > 0)
                {
                    builder.Append('/');
                }
                builder.Append(segment);

                var hash = ComputeUnityBonePathHash(builder.ToString());
                if (hash != 0)
                {
                    outputHashes.Add(hash);
                }
            }

            return true;
        }

        private static void AddAnimatorControllerClipRelations(
            SerializedFile fallbackSourceFile,
            SemanticAssetRelations relations,
            RuntimeAnimatorController controller,
            Animator animator,
            string animatorId,
            string avatarId,
            int avatarFileId,
            long avatarPathId,
            string primaryMeshId,
            string groupId,
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

            var relationKind = controller is AnimatorOverrideController
                ? "AnimatorOverrideController"
                : "AnimatorController";

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

                if (!string.IsNullOrEmpty(avatarId)
                    || !string.IsNullOrEmpty(primaryMeshId)
                    || !string.IsNullOrEmpty(groupId))
                {
                    relations.AnimationClipTargets.Add(new SemanticAnimationClipTargetRelation(
                        clipId,
                        animatorId,
                        avatarId,
                        primaryMeshId,
                        groupId,
                        relationKind,
                        10000 - Math.Min(slotIndex, 100) - (depth * 100)));
                }

                slotIndex++;
            }

            if (controller is AnimatorOverrideController overrideController
                && overrideController.m_Controller != null
                && !overrideController.m_Controller.IsNull)
            {
                var baseControllerId = GetSemanticAssetIdFromPPtr(controllerSourceFile, overrideController.m_Controller, ClassIDType.RuntimeAnimatorController);
                if (!string.IsNullOrEmpty(controllerId) && !string.IsNullOrEmpty(baseControllerId))
                {
                    AddAssetEdgeById(
                        relations,
                        controllerId,
                        "AnimatorController",
                        "m_Controller",
                        0,
                        baseControllerId,
                        0,
                        overrideController.m_PathID,
                        overrideController.m_Controller.m_FileID,
                        overrideController.m_Controller.m_PathID,
                        isResolved: true);
                }

                var baseController = ResolveRuntimeAnimatorControllerBackground(controllerSourceFile, overrideController.m_Controller);
                if (baseController != null && !ReferenceEquals(baseController, controller))
                {
                    AddAnimatorControllerClipRelations(
                        controllerSourceFile,
                        relations,
                        baseController,
                        animator,
                        animatorId,
                        avatarId,
                        avatarFileId,
                        avatarPathId,
                        primaryMeshId,
                        groupId,
                        depth + 1);
                }
            }
        }

        private static void IndexRuntimeAnimatorControllerRelations(
            SerializedFile sourceFile,
            RuntimeAnimatorController controller,
            SemanticAssetRelations relations)
        {
            if (controller == null)
            {
                return;
            }

            var controllerId = GetSemanticAssetId(controller);
            if (string.IsNullOrEmpty(controllerId))
            {
                return;
            }

            var slotIndex = 0;
            foreach (var clipRef in EnumerateAnimationClipPointers(controller))
            {
                if (clipRef == null || clipRef.IsNull)
                {
                    slotIndex++;
                    continue;
                }

                var clipId = GetSemanticAssetIdFromPPtr(sourceFile, clipRef, ClassIDType.AnimationClip);
                if (!string.IsNullOrEmpty(clipId))
                {
                    AddAssetEdge(
                        relations,
                        controller,
                        "AnimationClip",
                        "m_AnimationClips",
                        slotIndex,
                        clipId,
                        clipRef.m_FileID,
                        clipRef.m_PathID);
                }

                slotIndex++;
            }

            if (controller is AnimatorOverrideController overrideController
                && overrideController.m_Controller != null
                && !overrideController.m_Controller.IsNull)
            {
                var baseControllerId = GetSemanticAssetIdFromPPtr(sourceFile, overrideController.m_Controller, ClassIDType.RuntimeAnimatorController);
                if (!string.IsNullOrEmpty(baseControllerId))
                {
                    AddAssetEdge(
                        relations,
                        overrideController,
                        "AnimatorController",
                        "m_Controller",
                        0,
                        baseControllerId,
                        overrideController.m_Controller.m_FileID,
                        overrideController.m_Controller.m_PathID);
                }
            }
        }

        private static void AddLegacyAnimationRelations(
            SerializedFile sourceFile,
            Animation legacyAnimation,
            List<SkinnedMeshSemanticBinding> skinnedMeshBindings,
            List<RendererMeshSemanticBinding> meshRendererBindings,
            int animatorCount,
            SemanticAssetRelations relations)
        {
            if (legacyAnimation?.m_Animations == null || legacyAnimation.m_Animations.Length == 0)
            {
                return;
            }

            var animGameObject = ResolveGameObjectBackground(sourceFile, legacyAnimation.m_GameObject);
            var animationComponentId = GetSemanticAssetId(legacyAnimation);
            var rootGameObjectId = animGameObject != null
                ? GetSemanticGameObjectId(sourceFile, legacyAnimation.m_GameObject, animGameObject)
                : string.Empty;

            var meshMatches = FindRendererMeshesForAnimator(
                sourceFile,
                animGameObject,
                skinnedMeshBindings,
                meshRendererBindings,
                Math.Max(1, animatorCount)).ToList();
            var primaryMeshId = string.Empty;
            var meshSlotIndex = 0;
            var seenMeshes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var binding in meshMatches)
            {
                if (string.IsNullOrEmpty(binding.MeshId) || !seenMeshes.Add(binding.MeshId))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(primaryMeshId))
                {
                    primaryMeshId = binding.MeshId;
                }

                AddAssetEdge(
                    relations,
                    legacyAnimation,
                    "Mesh",
                    "AnimatorMesh",
                    meshSlotIndex,
                    binding.MeshId,
                    binding.MeshFileId,
                    binding.MeshPathId);
                meshSlotIndex++;
            }

            var groupId = !string.IsNullOrEmpty(rootGameObjectId) && meshMatches.Count > 1
                ? $"modelgroup:sceneobject:{rootGameObjectId}"
                : string.Empty;

            var slotIndex = 0;
            foreach (var clipRef in legacyAnimation.m_Animations)
            {
                if (clipRef == null || clipRef.IsNull)
                {
                    slotIndex++;
                    continue;
                }

                var clipId = GetSemanticAssetIdFromPPtr(sourceFile, clipRef, ClassIDType.AnimationClip);
                if (string.IsNullOrEmpty(clipId))
                {
                    slotIndex++;
                    continue;
                }

                AddAssetEdge(
                    relations,
                    legacyAnimation,
                    "AnimationClip",
                    "LegacyAnimationClip",
                    slotIndex,
                    clipId,
                    clipRef.m_FileID,
                    clipRef.m_PathID);

                if (!string.IsNullOrEmpty(primaryMeshId) || !string.IsNullOrEmpty(groupId))
                {
                    relations.AnimationClipTargets.Add(new SemanticAnimationClipTargetRelation(
                        clipId,
                        animationComponentId,
                        string.Empty,
                        primaryMeshId,
                        groupId,
                        "LegacyAnimation",
                        9700 - Math.Min(slotIndex, 100)));
                }

                slotIndex++;
            }
        }

        private static List<SkinnedMeshSemanticBinding> SelectSingleRootBindingsForAvatar(
            SerializedFile sourceFile,
            List<SkinnedMeshSemanticBinding> matchedBindings,
            Avatar avatar,
            HashSet<uint> rigHashes,
            out GameObject? selectedRootGo,
            out Animator? selectedAnimator)
        {
            selectedRootGo = null;
            selectedAnimator = null;
            if (matchedBindings.Count == 0)
            {
                return matchedBindings;
            }

            var buckets = new Dictionary<Transform, List<SkinnedMeshSemanticBinding>>();
            var unrooted = new List<SkinnedMeshSemanticBinding>();
            foreach (var binding in matchedBindings)
            {
                var rootTr = FindTopRootTransform(binding.GameObject?.m_Transform);
                if (rootTr != null)
                {
                    if (!buckets.TryGetValue(rootTr, out var list))
                    {
                        list = new List<SkinnedMeshSemanticBinding>();
                        buckets[rootTr] = list;
                    }
                    list.Add(binding);
                }
                else
                {
                    unrooted.Add(binding);
                }
            }

            if (buckets.Count == 0)
            {
                return unrooted;
            }

            string avatarStem = NormalizeModelGroupStem(avatar.m_Name);
            Transform? bestRoot = null;
            List<SkinnedMeshSemanticBinding>? bestList = null;
            GameObject? bestRootGo = null;
            Animator? bestRootAnim = null;
            double bestScore = double.NegativeInfinity;

            foreach (var (rootTr, list) in buckets)
            {
                var rootGo = TryGetLoadedObjectNoDeserialize(rootTr.assetsFile ?? sourceFile, rootTr.m_GameObject);
                var rootAnim = rootGo?.m_Animator;
                bool hasNormalScale = IsReasonablePositiveRootScale(rootTr.m_LocalScale);

                int totalOverlap = 0;
                int totalBones = 0;
                foreach (var b in list)
                {
                    if (b.Mesh?.m_BoneNameHashes != null && b.Mesh.m_BoneNameHashes.Length > 0)
                    {
                        totalBones += b.Mesh.m_BoneNameHashes.Length;
                        for (int h = 0; h < b.Mesh.m_BoneNameHashes.Length; h++)
                        {
                            if (rigHashes.Contains(b.Mesh.m_BoneNameHashes[h]))
                            {
                                totalOverlap++;
                            }
                        }
                    }
                }

                double overlapRatio = totalBones > 0 ? (double)totalOverlap / totalBones : 0.0;
                double score = 0.0;
                if (hasNormalScale) score += 1000.0;
                if (rootAnim != null
                    && rootAnim.m_Avatar != null
                    && ReferenceEquals(TryGetLoadedObjectNoDeserialize(rootAnim.assetsFile ?? sourceFile, rootAnim.m_Avatar), avatar))
                {
                    score += 500.0;
                }
                string rootStem = NormalizeModelGroupStem(rootGo?.m_Name);
                if (!string.IsNullOrEmpty(avatarStem) && !string.IsNullOrEmpty(rootStem))
                {
                    if (string.Equals(rootStem, avatarStem, StringComparison.OrdinalIgnoreCase)) score += 300.0;
                    else if (rootStem.Contains(avatarStem, StringComparison.OrdinalIgnoreCase)
                        || avatarStem.Contains(rootStem, StringComparison.OrdinalIgnoreCase)) score += 150.0;
                }
                score += overlapRatio * 200.0;
                score += Math.Min(list.Count, 16) * 5.0;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestRoot = rootTr;
                    bestList = list;
                    bestRootGo = rootGo;
                    bestRootAnim = rootAnim;
                }
            }

            if (bestList == null)
            {
                return matchedBindings;
            }

            selectedRootGo = bestRootGo;
            selectedAnimator = bestRootAnim;
            return bestList;
        }

        internal static Transform? FindTopRootTransform(Transform? start)
        {
            if (start == null)
            {
                return null;
            }

            var current = start;
            var visited = new HashSet<Transform> { current };
            while (current.m_Father != null && !current.m_Father.IsNull)
            {
                var father = TryGetLoadedObjectNoDeserialize(current.assetsFile, current.m_Father);
                if (father == null || !visited.Add(father))
                {
                    break;
                }
                current = father;
            }
            return current;
        }

        internal static bool IsReasonablePositiveRootScale(Vector3 scale)
        {
            return float.IsFinite(scale.X) && float.IsFinite(scale.Y) && float.IsFinite(scale.Z)
                && scale.X > 0.0001f && scale.Y > 0.0001f && scale.Z > 0.0001f
                && scale.X < 100f && scale.Y < 100f && scale.Z < 100f;
        }

        internal static string NormalizeModelGroupStem(string? rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return string.Empty;
            }

            var cleaned = rawName.Replace("Avatar", string.Empty, StringComparison.OrdinalIgnoreCase);
            var sb = new System.Text.StringBuilder(cleaned.Length);
            for (int i = 0; i < cleaned.Length; i++)
            {
                if (char.IsLetterOrDigit(cleaned[i]))
                {
                    sb.Append(char.ToLowerInvariant(cleaned[i]));
                }
            }
            return sb.ToString();
        }

        private static void AddAvatarRigRelations(
            SerializedFile sourceFile,
            Avatar avatar,
            List<SkinnedMeshSemanticBinding> skinnedMeshBindings,
            AssetStudio.Object[] objectsSnapshot,
            SemanticAssetRelations relations)
        {
            if (avatar == null)
            {
                return;
            }

            var avatarId = GetSemanticAssetId(avatar);
            if (string.IsNullOrEmpty(avatarId))
            {
                return;
            }

            var rigHashes = ExtractAvatarRigBoneHashes(avatar);
            var avatarCountInFile = objectsSnapshot.OfType<Avatar>().Count();
            var primaryMeshId = string.Empty;
            var groupId = string.Empty;
            var animatorId = string.Empty;

            foreach (var existingGroup in relations.ModelGroups)
            {
                if (string.Equals(existingGroup.AvatarAssetId, avatarId, StringComparison.Ordinal))
                {
                    groupId = existingGroup.GroupId;
                    animatorId = existingGroup.AnimatorAssetId;
                    break;
                }
            }

            if (skinnedMeshBindings.Count > 0)
            {
                var matchedBindings = new List<SkinnedMeshSemanticBinding>();
                foreach (var binding in skinnedMeshBindings)
                {
                    if (string.IsNullOrEmpty(binding.MeshId))
                    {
                        continue;
                    }

                    if (avatarCountInFile == 1 || DoesSkinnedMeshMatchAvatarBones(sourceFile, binding, avatar, rigHashes))
                    {
                        matchedBindings.Add(binding);
                    }
                }

                var slotIndex = 0;
                var seenMeshes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var binding in matchedBindings)
                {
                    if (!seenMeshes.Add(binding.MeshId))
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(primaryMeshId))
                    {
                        primaryMeshId = binding.MeshId;
                    }

                    AddAssetEdge(
                        relations,
                        avatar,
                        "Mesh",
                        "AvatarMesh",
                        slotIndex,
                        binding.MeshId,
                        binding.MeshFileId,
                        binding.MeshPathId);
                    slotIndex++;
                }

                if (string.IsNullOrEmpty(groupId) && matchedBindings.Count >= 2)
                {
                    // Group matched SkinnedMeshRenderers by their top-level prefab/root Transform so unrelated characters
                    // sharing common humanoid bone names never get merged into a single multi-character ModelGroup.
                    var selectedBindings = SelectSingleRootBindingsForAvatar(sourceFile, matchedBindings, avatar, rigHashes, out var selectedRootGo, out var selectedAnimator);
                    if (selectedBindings.Count >= 2)
                    {
                        groupId = $"modelgroup:avatar:{avatarId}";
                        var groupName = !string.IsNullOrWhiteSpace(selectedRootGo?.m_Name)
                            ? selectedRootGo!.m_Name
                            : !string.IsNullOrWhiteSpace(avatar.m_Name)
                                ? avatar.m_Name.Replace("Avatar", string.Empty, StringComparison.OrdinalIgnoreCase).Trim()
                                : "Avatar Model";
                        if (string.IsNullOrWhiteSpace(groupName))
                        {
                            groupName = avatar.m_Name ?? "Avatar Model";
                        }

                        var selectedRootGoId = selectedRootGo != null ? GetSemanticAssetId(selectedRootGo) : string.Empty;
                        var selectedAnimatorId = selectedAnimator != null ? GetSemanticAssetId(selectedAnimator) : string.Empty;

                        relations.ModelGroups.Add(new SemanticModelGroupRelation(
                            groupId,
                            "Animator",
                            groupName,
                            selectedRootGoId,
                            selectedRootGo?.m_Name ?? groupName,
                            selectedAnimatorId,
                            avatarId,
                            string.Empty,
                            sourceFile.fileName ?? string.Empty,
                            85,
                            "Avatar skinned mesh co-location"));

                        var partSlot = 0;
                        var seenParts = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var binding in selectedBindings)
                        {
                            var rendererId = GetSemanticAssetId(binding.Renderer);
                            if (string.IsNullOrEmpty(rendererId) || !seenParts.Add($"{binding.MeshId}\u001f{rendererId}"))
                            {
                                continue;
                            }

                            var goId = binding.GameObject != null ? GetSemanticAssetId(binding.GameObject) : string.Empty;
                            var isPartActive = IsRendererActiveInHierarchy(sourceFile, binding.Renderer, binding.GameObject, selectedRootGo);
                            relations.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                                groupId,
                                binding.MeshId,
                                rendererId,
                                "SkinnedMeshRenderer",
                                goId,
                                binding.GameObject?.m_Name ?? string.Empty,
                                partSlot++,
                                BuildLocalToWorldMatrix(sourceFile, binding.GameObject),
                                isPartActive ? 85 : 35,
                                isPartActive ? "Avatar skinned mesh co-location" : "Avatar skinned mesh co-location (inactive)"));
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(primaryMeshId))
            {
                foreach (var mesh in objectsSnapshot.OfType<Mesh>())
                {
                    if (mesh.m_BoneNameHashes == null || mesh.m_BoneNameHashes.Length == 0)
                    {
                        continue;
                    }

                    var overlap = mesh.m_BoneNameHashes.Count(rigHashes.Contains);
                    if (IsStrongMeshAvatarMatch(mesh.m_BoneNameHashes.Length, overlap))
                    {
                        var meshId = GetSemanticAssetId(mesh);
                        if (!string.IsNullOrEmpty(meshId))
                        {
                            if (string.IsNullOrEmpty(primaryMeshId))
                            {
                                primaryMeshId = meshId;
                            }

                            AddAssetEdge(
                                relations,
                                avatar,
                                "Mesh",
                                "AvatarMesh",
                                0,
                                meshId,
                                0,
                                mesh.m_PathID);
                        }
                    }
                }
            }

            var emittedCount = 0;
            foreach (var hash in rigHashes)
            {
                if (hash == 0)
                {
                    continue;
                }

                relations.RigBoneHashes.Add(new SemanticRigBoneHashEntry(
                    avatarId,
                    animatorId,
                    primaryMeshId,
                    groupId,
                    hash));

                emittedCount++;
                if (emittedCount >= MaxRigBoneHashProbes)
                {
                    break;
                }
            }
        }

        private static bool DoesSkinnedMeshMatchAvatarBones(
            SerializedFile sourceFile,
            SkinnedMeshSemanticBinding binding,
            Avatar avatar,
            HashSet<uint> avatarBoneHashes)
        {
            if (binding.Mesh?.m_BoneNameHashes != null && binding.Mesh.m_BoneNameHashes.Length > 0)
            {
                var overlap = binding.Mesh.m_BoneNameHashes.Count(avatarBoneHashes.Contains);
                if (IsStrongMeshAvatarMatch(binding.Mesh.m_BoneNameHashes.Length, overlap))
                {
                    return true;
                }
            }

            if (binding.Renderer?.m_Bones != null && binding.Renderer.m_Bones.Length > 0 && avatarBoneHashes.Count > 0)
            {
                var matched = 0;
                var checkedBones = 0;
                var step = Math.Max(1, binding.Renderer.m_Bones.Length / 16);
                for (var i = 0; i < binding.Renderer.m_Bones.Length; i += step)
                {
                    var boneTransform = ResolveTransformBackground(sourceFile, binding.Renderer.m_Bones[i]);
                    var boneGo = boneTransform != null ? ResolveGameObjectBackground(sourceFile, boneTransform.m_GameObject) : null;
                    if (!string.IsNullOrWhiteSpace(boneGo?.m_Name))
                    {
                        checkedBones++;
                        var leafHash = ComputeUnityBonePathHash(boneGo!.m_Name);
                        if (leafHash != 0 && avatarBoneHashes.Contains(leafHash))
                        {
                            matched++;
                        }
                    }
                }

                if (checkedBones > 0 && matched >= Math.Max(2, checkedBones / 3))
                {
                    return true;
                }
            }

            var avatarBaseName = (avatar.m_Name ?? string.Empty).Replace("Avatar", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
            var goName = binding.GameObject?.m_Name ?? string.Empty;
            return avatarBaseName.Length >= 3
                && goName.Length >= 3
                && (goName.Contains(avatarBaseName, StringComparison.OrdinalIgnoreCase)
                    || avatarBaseName.Contains(goName, StringComparison.OrdinalIgnoreCase));
        }

        public static HashSet<uint> ExtractAvatarBindingPathHashes(Avatar avatar)
        {
            var hashes = new HashSet<uint>();
            if (avatar.m_Avatar?.m_AvatarSkeleton?.m_ID != null)
            {
                foreach (var id in avatar.m_Avatar.m_AvatarSkeleton.m_ID)
                {
                    if (id != 0)
                    {
                        hashes.Add(id);
                    }
                }
            }

            if (avatar.m_TOS != null)
            {
                foreach (var pair in avatar.m_TOS)
                {
                    if (pair.Key != 0)
                    {
                        hashes.Add(pair.Key);
                    }
                }
            }

            return hashes;
        }

        public static HashSet<uint> ExtractAvatarRigBoneHashes(Avatar avatar)
        {
            var hashes = new HashSet<uint>();
            if (avatar.m_Avatar?.m_AvatarSkeleton?.m_ID != null)
            {
                foreach (var id in avatar.m_Avatar.m_AvatarSkeleton.m_ID)
                {
                    if (id != 0)
                    {
                        hashes.Add(id);
                    }
                }
            }

            if (avatar.m_TOS != null)
            {
                foreach (var pair in avatar.m_TOS)
                {
                    if (pair.Key != 0)
                    {
                        hashes.Add(pair.Key);
                    }

                    if (!string.IsNullOrWhiteSpace(pair.Value))
                    {
                        var leafName = GetLeafBoneName(pair.Value);
                        if (!string.IsNullOrEmpty(leafName))
                        {
                            var leafHash = ComputeUnityBonePathHash(leafName);
                            if (leafHash != 0)
                            {
                                hashes.Add(leafHash);
                            }
                        }
                    }
                }
            }

            return hashes;
        }

        private static void AddAnimationClipFingerprintRelations(
            SerializedFile sourceFile,
            AnimationClip clip,
            SemanticAssetRelations relations)
        {
            if (clip == null)
            {
                return;
            }

            if (clip.m_PPtrCurves != null && clip.m_PPtrCurves.Length > 0)
            {
                return;
            }

            var clipId = GetSemanticAssetId(clip);
            if (string.IsNullOrEmpty(clipId))
            {
                clipId = AssetHandle.BuildUniqueID(sourceFile, clip.m_PathID);
            }

            if (string.IsNullOrEmpty(clipId))
            {
                return;
            }

            var bindingPaths = GetTransformBindingPathsBackground(clip);
            if (bindingPaths.Count == 0)
            {
                return;
            }

            var probes = SelectDeterministicBoneHashProbes(bindingPaths, MaxClipBoneHashProbes);
            foreach (var probe in probes)
            {
                relations.ClipBoneHashes.Add(new SemanticClipBoneHashEntry(
                    clipId,
                    probe,
                    bindingPaths.Count));
            }
        }

        public static uint[] SelectDeterministicBoneHashProbes(IReadOnlyCollection<uint> boneHashes, int maxProbes = MaxClipBoneHashProbes)
        {
            if (boneHashes == null || boneHashes.Count == 0 || maxProbes <= 0)
            {
                return Array.Empty<uint>();
            }

            var sorted = boneHashes.Where(h => h != 0).Distinct().OrderBy(h => h).ToArray();
            if (sorted.Length <= maxProbes)
            {
                return sorted;
            }

            var result = new uint[maxProbes];
            for (var i = 0; i < maxProbes; i++)
            {
                var idx = (int)((long)i * (sorted.Length - 1) / Math.Max(1, maxProbes - 1));
                result[i] = sorted[idx];
            }

            return result;
        }

        private static void AddSkinnedMeshFallbackBoneHashes(
            SerializedFile sourceFile,
            List<SkinnedMeshSemanticBinding> skinnedMeshBindings,
            SemanticAssetRelations relations)
        {
            if (skinnedMeshBindings.Count == 0)
            {
                return;
            }

            var coveredMeshes = new HashSet<string>(
                relations.RigBoneHashes
                    .Where(r => !string.IsNullOrEmpty(r.MeshAssetId))
                    .Select(r => r.MeshAssetId),
                StringComparer.Ordinal);

            foreach (var binding in skinnedMeshBindings)
            {
                if (string.IsNullOrEmpty(binding.MeshId) || !coveredMeshes.Add(binding.MeshId))
                {
                    continue;
                }

                var boneHashes = new HashSet<uint>();
                var hasTransformBones = binding.Renderer?.m_Bones != null && binding.Renderer.m_Bones.Length > 0;
                var meshForHashes = binding.Mesh;
                if (meshForHashes == null && !hasTransformBones && binding.Renderer?.m_Mesh != null)
                {
                    // Rare fallback: only resolve Mesh when SkinnedMeshRenderer has no Transform m_Bones.
                    meshForHashes = ResolveMeshBackground(sourceFile, binding.Renderer.m_Mesh);
                }

                if (meshForHashes?.m_BoneNameHashes != null)
                {
                    foreach (var hash in meshForHashes.m_BoneNameHashes)
                    {
                        if (hash != 0)
                        {
                            boneHashes.Add(hash);
                        }
                    }
                }

                if (hasTransformBones)
                {
                    var bones = binding.Renderer!.m_Bones!;
                    var pathSegments = new List<string>(16);
                    var visitedTransforms = new HashSet<long>();
                    var primaryHashes = new HashSet<uint>();
                    var secondaryHashes = new HashSet<uint>();
                    for (var i = 0; i < bones.Length; i++)
                    {
                        var transform = ResolveTransformBackground(sourceFile, bones[i]);
                        if (transform == null)
                        {
                            continue;
                        }

                        pathSegments.Clear();
                        visitedTransforms.Clear();
                        Transform? current = transform;
                        for (var depth = 0; current != null && depth < 64; depth++)
                        {
                            if (!visitedTransforms.Add(current.m_PathID))
                            {
                                break;
                            }

                            var currentFile = current.assetsFile ?? sourceFile;
                            var go = ResolveGameObjectBackground(currentFile, current.m_GameObject);
                            if (!string.IsNullOrWhiteSpace(go?.m_Name))
                            {
                                pathSegments.Add(go!.m_Name);
                            }

                            if (current.m_Father == null || current.m_Father.IsNull)
                            {
                                break;
                            }

                            current = ResolveTransformBackground(currentFile, current.m_Father);
                        }

                        if (pathSegments.Count == 0)
                        {
                            continue;
                        }

                        var leafHash = ComputeUnityBonePathHash(pathSegments[0]);
                        pathSegments.Reverse();
                        var fullPath = string.Join("/", pathSegments);
                        var fullHash = ComputeUnityBonePathHash(fullPath);

                        // Prioritize root-relative paths (strip=1, strip=2) and full path across ALL bones first,
                        // keeping leaf names as secondary so large rigs are not truncated before reaching all bones.
                        for (var strip = 1; strip < pathSegments.Count - 1 && strip <= 2; strip++)
                        {
                            var relPath = string.Join("/", pathSegments.Skip(strip));
                            var relHash = ComputeUnityBonePathHash(relPath);
                            if (relHash != 0)
                            {
                                primaryHashes.Add(relHash);
                            }
                        }

                        if (fullHash != 0)
                        {
                            secondaryHashes.Add(fullHash);
                        }

                        if (leafHash != 0)
                        {
                            secondaryHashes.Add(leafHash);
                        }
                    }

                    foreach (var h in primaryHashes)
                    {
                        boneHashes.Add(h);
                    }

                    if (boneHashes.Count < MaxRigBoneHashProbes)
                    {
                        foreach (var h in secondaryHashes)
                        {
                            boneHashes.Add(h);
                            if (boneHashes.Count >= MaxRigBoneHashProbes)
                            {
                                break;
                            }
                        }
                    }
                }

                var selectedHashes = SelectDeterministicBoneHashProbes(boneHashes, MaxRigBoneHashProbes);
                var groupId = relations.ModelGroupMeshes
                    .FirstOrDefault(m => string.Equals(m.MeshAssetId, binding.MeshId, StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(m.GroupId))
                    ?.GroupId ?? string.Empty;
                for (var i = 0; i < selectedHashes.Length; i++)
                {
                    relations.RigBoneHashes.Add(new SemanticRigBoneHashEntry(
                        string.Empty,
                        string.Empty,
                        binding.MeshId,
                        groupId,
                        selectedHashes[i]));
                }
            }
        }

        public static uint ComputeUnityBonePathHash(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return 0;
            }

            var normalized = path.Replace('\\', '/').Trim('/');
            if (normalized.Length == 0)
            {
                return 0;
            }

            var table = SevenZip.CRC.Table;
            uint crc = 0xFFFFFFFFu;
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(normalized);
            if (byteCount <= 256)
            {
                Span<byte> buffer = stackalloc byte[byteCount];
                System.Text.Encoding.UTF8.GetBytes(normalized.AsSpan(), buffer);
                for (var i = 0; i < buffer.Length; i++)
                {
                    crc = table[((byte)crc) ^ buffer[i]] ^ (crc >> 8);
                }
            }
            else
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
                for (var i = 0; i < bytes.Length; i++)
                {
                    crc = table[((byte)crc) ^ bytes[i]] ^ (crc >> 8);
                }
            }

            return crc ^ 0xFFFFFFFFu;
        }

        public static string GetLeafBoneName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var normalized = path.Replace('\\', '/').Trim('/');
            var slashIdx = normalized.LastIndexOf('/');
            return slashIdx >= 0 && slashIdx + 1 < normalized.Length
                ? normalized.Substring(slashIdx + 1)
                : normalized;
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

        public static void BuildAnimationPreviewIndexesBackground(
            IReadOnlyList<SerializedFile> assetsFileList,
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

            var avatarIndexByTosHash = new Dictionary<uint, List<int>>();
            var avatarIndexBySkelHash = new Dictionary<uint, List<int>>();
            var avatarSkeletonHashes = new HashSet<uint>[avatars.Length];
            var avatarCleanNames = new string[avatars.Length];
            var avatarSearchKeys = new string[avatars.Length];

            for (var a = 0; a < avatars.Length; a++)
            {
                var avatar = avatars[a];
                avatarCleanNames[a] = avatar.m_Name.Replace("Avatar", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                avatarSearchKeys[a] = NormalizeAnimatorSearchKey(avatarCleanNames[a]);

                if (avatar.m_TOS != null && avatar.m_TOS.Length > 0)
                {
                    var seenTos = new HashSet<uint>(avatar.m_TOS.Length);
                    foreach (var pair in avatar.m_TOS)
                    {
                        if (pair.Key != 0 && seenTos.Add(pair.Key))
                        {
                            if (!avatarIndexByTosHash.TryGetValue(pair.Key, out var list))
                            {
                                list = new List<int>();
                                avatarIndexByTosHash[pair.Key] = list;
                            }

                            list.Add(a);
                        }
                    }
                }

                if (avatar.m_Avatar?.m_AvatarSkeleton?.m_ID != null && avatar.m_Avatar.m_AvatarSkeleton.m_ID.Length > 0)
                {
                    var skelSet = new HashSet<uint>(avatar.m_Avatar.m_AvatarSkeleton.m_ID);
                    avatarSkeletonHashes[a] = skelSet;
                    foreach (var boneId in skelSet)
                    {
                        if (!avatarIndexBySkelHash.TryGetValue(boneId, out var list))
                        {
                            list = new List<int>();
                            avatarIndexBySkelHash[boneId] = list;
                        }

                        list.Add(a);
                    }
                }
                else
                {
                    avatarSkeletonHashes[a] = new HashSet<uint>();
                }
            }

            var animationClipTransformBindingsCache = new Dictionary<AnimationClip, HashSet<uint>>(clips.Length);
            foreach (var clip in clips)
            {
                animationClipTransformBindingsCache[clip] = GetTransformBindingPathsBackground(clip);
            }

            var avatarMeshCache = new Dictionary<Avatar, Mesh?>(avatars.Length);
            var meshToSkeletonHashes = new HashSet<uint>[meshes.Length];
            for (var m = 0; m < meshes.Length; m++)
            {
                meshToSkeletonHashes[m] = new HashSet<uint>(meshes[m].m_BoneNameHashes);
            }

            var meshIndexByBoneHash = new Dictionary<uint, List<int>>();
            for (var m = 0; m < meshes.Length; m++)
            {
                foreach (var hash in meshToSkeletonHashes[m])
                {
                    if (!meshIndexByBoneHash.TryGetValue(hash, out var list))
                    {
                        list = new List<int>();
                        meshIndexByBoneHash[hash] = list;
                    }

                    list.Add(m);
                }
            }

            var overlapCounts = new Dictionary<int, int>();
            for (var a = 0; a < avatars.Length; a++)
            {
                var avatar = avatars[a];
                var skelHashes = avatarSkeletonHashes[a];
                if (skelHashes.Count == 0)
                {
                    avatarMeshCache[avatar] = null;
                    continue;
                }

                overlapCounts.Clear();
                foreach (var boneId in skelHashes)
                {
                    if (!meshIndexByBoneHash.TryGetValue(boneId, out var matchingMeshes))
                    {
                        continue;
                    }

                    foreach (var mIdx in matchingMeshes)
                    {
                        overlapCounts.TryGetValue(mIdx, out var count);
                        overlapCounts[mIdx] = count + 1;
                    }
                }

                Mesh? bestMesh = null;
                var bestScore = 0;
                var avatarName = avatarCleanNames[a];
                foreach (var kvp in overlapCounts)
                {
                    var mesh = meshes[kvp.Key];
                    var overlap = kvp.Value;
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

                avatarMeshCache[avatar] = bestMesh;
            }

            var meshAvatarCache = new Dictionary<Mesh, Avatar?>(meshes.Length);
            for (var m = 0; m < meshes.Length; m++)
            {
                var mesh = meshes[m];
                var meshHashes = meshToSkeletonHashes[m];
                overlapCounts.Clear();
                foreach (var hash in meshHashes)
                {
                    if (!avatarIndexBySkelHash.TryGetValue(hash, out var matchingAvatars))
                    {
                        continue;
                    }

                    foreach (var aIdx in matchingAvatars)
                    {
                        overlapCounts.TryGetValue(aIdx, out var count);
                        overlapCounts[aIdx] = count + 1;
                    }
                }

                Avatar? bestAvatar = null;
                var bestScore = 0;
                var meshName = mesh.m_Name.ToLowerInvariant();
                foreach (var kvp in overlapCounts)
                {
                    var avatar = avatars[kvp.Key];
                    var overlap = kvp.Value;
                    if (!IsStrongMeshAvatarMatch(meshHashes.Count, overlap))
                    {
                        continue;
                    }

                    var score = overlap * 100;
                    if (avatar.assetsFile == mesh.assetsFile) score += 20;
                    var avatarNameLower = avatarCleanNames[kvp.Key].ToLowerInvariant();
                    if (!string.IsNullOrEmpty(avatarNameLower) && meshName.Contains(avatarNameLower)) score += 15;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestAvatar = avatar;
                    }
                }

                meshAvatarCache[mesh] = bestAvatar;
            }

            var animationClipAvatarCache = new Dictionary<AnimationClip, Avatar?>(clips.Length);
            foreach (var clip in clips)
            {
                animationClipTransformBindingsCache.TryGetValue(clip, out var bindingPaths);
                if (bindingPaths == null || bindingPaths.Count == 0)
                {
                    animationClipAvatarCache[clip] = null;
                    continue;
                }

                overlapCounts.Clear();
                foreach (var pathHash in bindingPaths)
                {
                    if (!avatarIndexByTosHash.TryGetValue(pathHash, out var matchingAvatars))
                    {
                        continue;
                    }

                    foreach (var aIdx in matchingAvatars)
                    {
                        overlapCounts.TryGetValue(aIdx, out var count);
                        overlapCounts[aIdx] = count + 1;
                    }
                }

                Avatar? bestAvatar = null;
                var bestScore = 0;
                var clipName = NormalizeAnimatorSearchKey(clip.m_Name);
                foreach (var kvp in overlapCounts)
                {
                    var overlap = kvp.Value;
                    if (!IsStrongAnimationAvatarMatch(bindingPaths.Count, overlap))
                    {
                        continue;
                    }

                    var avatar = avatars[kvp.Key];
                    var score = overlap * 100;
                    if (avatar.assetsFile == clip.assetsFile) score += 20;
                    var avatarSearchKey = avatarSearchKeys[kvp.Key];
                    if (!string.IsNullOrEmpty(avatarSearchKey) && clipName.Contains(avatarSearchKey, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 15;
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestAvatar = avatar;
                    }
                }

                animationClipAvatarCache[clip] = bestAvatar;
            }

            animationClipAvatarCacheOut = animationClipAvatarCache;
            avatarMeshCacheOut = avatarMeshCache;
            meshAvatarCacheOut = meshAvatarCache;
            animationClipTransformBindingsCacheOut = animationClipTransformBindingsCache;
        }

        public static HashSet<uint> GetTransformBindingPathsBackground(AnimationClip clip)
        {
            var result = new HashSet<uint>();
            if (clip == null)
            {
                return result;
            }

            var bindings = clip.m_ClipBindingConstant;
            if (bindings == null && clip.m_MuscleClip?.m_Clip != null)
            {
                bindings = clip.m_MuscleClip.m_Clip.ConvertValueArrayToGenericBinding();
            }

            if (bindings?.genericBindings != null)
            {
                foreach (var binding in bindings.genericBindings)
                {
                    if (binding.typeID == ClassIDType.Transform && binding.path != 0)
                    {
                        result.Add(binding.path);
                    }
                }
            }

            if (result.Count == 0)
            {
                void AddLegacyPath(string? curvePath)
                {
                    if (string.IsNullOrWhiteSpace(curvePath))
                    {
                        return;
                    }

                    var fullHash = ComputeUnityBonePathHash(curvePath);
                    if (fullHash != 0)
                    {
                        result.Add(fullHash);
                    }

                    var leaf = GetLeafBoneName(curvePath);
                    if (!string.IsNullOrEmpty(leaf))
                    {
                        var leafHash = ComputeUnityBonePathHash(leaf);
                        if (leafHash != 0)
                        {
                            result.Add(leafHash);
                        }
                    }
                }

                foreach (var curve in clip.m_RotationCurves ?? Array.Empty<QuaternionCurve>())
                {
                    AddLegacyPath(curve?.path);
                }

                foreach (var curve in clip.m_CompressedRotationCurves ?? Array.Empty<CompressedAnimationCurve>())
                {
                    AddLegacyPath(curve?.m_Path);
                }

                foreach (var curve in clip.m_EulerCurves ?? Array.Empty<Vector3Curve>())
                {
                    AddLegacyPath(curve?.path);
                }

                foreach (var curve in clip.m_PositionCurves ?? Array.Empty<Vector3Curve>())
                {
                    AddLegacyPath(curve?.path);
                }

                foreach (var curve in clip.m_ScaleCurves ?? Array.Empty<Vector3Curve>())
                {
                    AddLegacyPath(curve?.path);
                }
            }

            return result;
        }

        public static bool IsStrongAnimationAvatarMatch(int bindingCount, int overlap)
        {
            if (bindingCount <= 0 || overlap <= 0)
            {
                return false;
            }

            var minimum = bindingCount < 6 ? bindingCount : Math.Max(6, bindingCount / 4);
            return overlap >= minimum;
        }

        public static bool IsStrongMeshAvatarMatch(int boneCount, int overlap)
        {
            if (boneCount <= 0 || overlap <= 0)
            {
                return false;
            }

            var minimum = boneCount < 6 ? boneCount : Math.Max(6, boneCount / 10);
            return overlap >= minimum;
        }

        public static string NormalizeAnimatorSearchKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return Path.GetFileNameWithoutExtension(value)
                .Replace("\\", "/", StringComparison.Ordinal)
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault()
                ?.Trim()
                .ToLowerInvariant() ?? string.Empty;
        }

    }
}
