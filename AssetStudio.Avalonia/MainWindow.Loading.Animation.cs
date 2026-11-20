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

            var meshMatches = FindSkinnedMeshesForAnimator(sourceFile, animatorGameObject, skinnedMeshBindings, animatorCount).ToList();
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
            foreach (var match in meshMatches)
            {
                var binding = match.Binding;
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
            IReadOnlyList<SkinnedMeshSemanticMatch> meshMatches)
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

            return groupId;
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

        internal const int MaxClipBoneHashProbes = 24;
        internal const int MaxRigBoneHashProbes = 192;

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
                    || !string.IsNullOrEmpty(groupId)
                    || !string.IsNullOrEmpty(animatorId))
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

            var meshMatches = FindSkinnedMeshesForAnimator(sourceFile, animGameObject, skinnedMeshBindings, Math.Max(1, animatorCount)).ToList();
            var primaryMeshId = string.Empty;
            var meshSlotIndex = 0;
            var seenMeshes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var match in meshMatches)
            {
                var binding = match.Binding;
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

                if (!string.IsNullOrEmpty(primaryMeshId) || !string.IsNullOrEmpty(groupId) || !string.IsNullOrEmpty(animationComponentId))
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

            // Link Avatar to same-file SkinnedMeshRenderers even when no Animator component exists in this file (e.g. FBX asset bundles).
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
                    groupId = $"modelgroup:avatar:{avatarId}";
                    var groupName = !string.IsNullOrWhiteSpace(avatar.m_Name)
                        ? avatar.m_Name.Replace("Avatar", string.Empty, StringComparison.OrdinalIgnoreCase).Trim()
                        : "Avatar Model";
                    if (string.IsNullOrWhiteSpace(groupName))
                    {
                        groupName = avatar.m_Name ?? "Avatar Model";
                    }

                    relations.ModelGroups.Add(new SemanticModelGroupRelation(
                        groupId,
                        "Animator",
                        groupName,
                        string.Empty,
                        groupName,
                        string.Empty,
                        avatarId,
                        string.Empty,
                        sourceFile.fileName ?? string.Empty,
                        85,
                        "Avatar skinned mesh co-location"));

                    var partSlot = 0;
                    var seenParts = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var binding in matchedBindings)
                    {
                        var rendererId = GetSemanticAssetId(binding.Renderer);
                        if (string.IsNullOrEmpty(rendererId) || !seenParts.Add($"{binding.MeshId}\u001f{rendererId}"))
                        {
                            continue;
                        }

                        var goId = binding.GameObject != null ? GetSemanticAssetId(binding.GameObject) : string.Empty;
                        relations.ModelGroupMeshes.Add(new SemanticModelGroupMeshRelation(
                            groupId,
                            binding.MeshId,
                            rendererId,
                            "SkinnedMeshRenderer",
                            goId,
                            binding.GameObject?.m_Name ?? string.Empty,
                            partSlot++,
                            BuildLocalToWorldMatrix(sourceFile, binding.GameObject),
                            85,
                            "Avatar skinned mesh co-location"));
                    }
                }
            }

            if (string.IsNullOrEmpty(primaryMeshId))
            {
                // Also check if any Mesh object in the same file has matching m_BoneNameHashes
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

        private static HashSet<uint> ExtractAvatarRigBoneHashes(Avatar avatar)
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

            // Skip 2D sprite/texture animation clips
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

        internal static uint[] SelectDeterministicBoneHashProbes(IReadOnlyCollection<uint> boneHashes, int maxProbes = MaxClipBoneHashProbes)
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

            // Only emit mesh-direct bone hashes for meshes not already covered by an Avatar in this file
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
                if (binding.Mesh?.m_BoneNameHashes != null)
                {
                    foreach (var hash in binding.Mesh.m_BoneNameHashes)
                    {
                        if (hash != 0)
                        {
                            boneHashes.Add(hash);
                        }
                    }
                }

                if (binding.Renderer?.m_Bones != null && binding.Renderer.m_Bones.Length > 0)
                {
                    var maxBonesToInspect = Math.Min(binding.Renderer.m_Bones.Length, 48);
                    for (var i = 0; i < maxBonesToInspect; i++)
                    {
                        var transform = ResolveTransformBackground(sourceFile, binding.Renderer.m_Bones[i]);
                        var go = transform != null ? ResolveGameObjectBackground(sourceFile, transform.m_GameObject) : null;
                        if (!string.IsNullOrWhiteSpace(go?.m_Name))
                        {
                            var hash = ComputeUnityBonePathHash(go!.m_Name);
                            if (hash != 0)
                            {
                                boneHashes.Add(hash);
                            }
                        }
                    }
                }

                var count = 0;
                foreach (var hash in boneHashes)
                {
                    relations.RigBoneHashes.Add(new SemanticRigBoneHashEntry(
                        string.Empty,
                        string.Empty,
                        binding.MeshId,
                        string.Empty,
                        hash));
                    count++;
                    if (count >= 48)
                    {
                        break;
                    }
                }
            }
        }

        private static void AddContainerAnimationRelations(
            SemanticAssetRelations relations,
            SerializedFile sourceFile,
            List<(string Container, List<PPtr<AssetStudio.Object>> References)> containerReferences)
        {
            if (containerReferences.Count == 0)
            {
                return;
            }

            foreach (var container in containerReferences)
            {
                if (string.IsNullOrWhiteSpace(container.Container))
                {
                    continue;
                }

                var clipIds = new List<string>();
                var avatarIds = new List<string>();
                var animatorIds = new List<string>();
                var meshIds = new List<string>();

                foreach (var reference in container.References)
                {
                    var handle = GetSemanticHandleFromPPtr(sourceFile, reference);
                    if (handle == null || string.IsNullOrWhiteSpace(handle.UniqueID))
                    {
                        continue;
                    }

                    switch (handle.Type)
                    {
                        case ClassIDType.AnimationClip:
                            if (!clipIds.Contains(handle.UniqueID, StringComparer.Ordinal))
                            {
                                clipIds.Add(handle.UniqueID);
                            }
                            break;
                        case ClassIDType.Avatar:
                            if (!avatarIds.Contains(handle.UniqueID, StringComparer.Ordinal))
                            {
                                avatarIds.Add(handle.UniqueID);
                            }
                            break;
                        case ClassIDType.Animator:
                            if (!animatorIds.Contains(handle.UniqueID, StringComparer.Ordinal))
                            {
                                animatorIds.Add(handle.UniqueID);
                            }
                            break;
                        case ClassIDType.Mesh:
                            if (!meshIds.Contains(handle.UniqueID, StringComparer.Ordinal))
                            {
                                meshIds.Add(handle.UniqueID);
                            }
                            break;
                    }
                }

                if (clipIds.Count == 0 || (avatarIds.Count == 0 && meshIds.Count == 0 && animatorIds.Count == 0))
                {
                    continue;
                }

                var primaryAvatarId = avatarIds.FirstOrDefault() ?? string.Empty;
                var primaryAnimatorId = animatorIds.FirstOrDefault() ?? string.Empty;
                var primaryMeshId = meshIds.FirstOrDefault() ?? string.Empty;
                foreach (var clipId in clipIds)
                {
                    relations.AnimationClipTargets.Add(new SemanticAnimationClipTargetRelation(
                        clipId,
                        primaryAnimatorId,
                        primaryAvatarId,
                        primaryMeshId,
                        string.Empty,
                        "Container",
                        6000));
                }
            }
        }

        internal static uint ComputeUnityBonePathHash(string? path)
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

        private static string GetLeafBoneName(string? path)
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

            // Precompute avatar bone-hash inverted indexes once instead of allocating HashSets in O(clips * avatars) loops.
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

        internal static HashSet<uint> GetTransformBindingPathsBackground(AnimationClip clip)
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
