using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AssetStudio;

namespace AssetStudio.Avalonia.Services
{
    public enum BatchAnimationSelectionKind
    {
        None = 0,
        ExplicitAnimator = 1,
        ExplicitAvatar = 2,
        ExplicitMesh = 3,
        ExplicitSkinnedMeshRenderer = 4,
        ExplicitGameObject = 5,
        ClipsOnly = 6
    }

    public sealed class BatchAnimationSelectionResolution
    {
        public BatchAnimationSelectionKind Kind { get; init; }
        public AssetItem? PrimaryModelAsset { get; init; }
        public Animator? Animator { get; init; }
        public Avatar? Avatar { get; init; }
        public Mesh? PrimaryMesh { get; init; }
        public IReadOnlyList<Mesh> SelectedMeshes { get; init; } = Array.Empty<Mesh>();
        public SkinnedMeshRenderer? SkinnedMeshRenderer { get; init; }
        public GameObject? RootGameObject { get; init; }
        public IReadOnlyList<GameObject> SelectedGameObjects { get; init; } = Array.Empty<GameObject>();
        public IReadOnlyList<AssetItem> SelectedClipAssets { get; init; } = Array.Empty<AssetItem>();
        public IReadOnlyList<AnimationClip> SelectedClips { get; init; } = Array.Empty<AnimationClip>();
        public string SuggestedBaseName { get; init; } = "model";

        public bool IsValid => Kind != BatchAnimationSelectionKind.None && (SelectedClipAssets.Count > 0 || SelectedClips.Count > 0);
    }

    public static class BatchAnimationExportHelper
    {
        public static bool CanExportSelectionWithAnimationClips(IReadOnlyList<AssetItem>? selectedAssets)
        {
            if (selectedAssets == null || selectedAssets.Count == 0)
            {
                return false;
            }

            var clipCount = 0;
            var hasExplicitTarget = false;
            for (var i = 0; i < selectedAssets.Count; i++)
            {
                var item = selectedAssets[i];
                if (item == null)
                {
                    continue;
                }

                if (item.Type == ClassIDType.AnimationClip || item.Asset is AnimationClip)
                {
                    clipCount++;
                }
                else if (IsSupportedModelTargetAsset(item))
                {
                    hasExplicitTarget = true;
                }
            }

            if (clipCount == 0)
            {
                return false;
            }

            return hasExplicitTarget || clipCount >= 1;
        }

        public static bool IsSupportedModelTargetAsset(AssetItem? item)
        {
            if (item == null)
            {
                return false;
            }

            if (item.Type is ClassIDType.Animator
                or ClassIDType.Avatar
                or ClassIDType.Mesh
                or ClassIDType.SkinnedMeshRenderer
                or ClassIDType.GameObject)
            {
                return true;
            }

            if (item.Asset is Animator or Avatar or Mesh or SkinnedMeshRenderer or GameObject)
            {
                return true;
            }

            return item.Type != ClassIDType.AnimationClip && item.TreeNode?.GameObject != null;
        }

        public static BatchAnimationSelectionResolution ResolveAssetListSelection(IReadOnlyList<AssetItem>? selectedAssets)
        {
            if (selectedAssets == null || selectedAssets.Count == 0)
            {
                return new BatchAnimationSelectionResolution { Kind = BatchAnimationSelectionKind.None };
            }

            var clipAssets = new List<AssetItem>();
            var resolvedClips = new List<AnimationClip>();
            var seenClipAssets = new HashSet<AssetItem>();
            var seenClips = new HashSet<AnimationClip>();

            AssetItem? animatorItem = null;
            AssetItem? avatarItem = null;
            AssetItem? smrItem = null;
            AssetItem? firstMeshItem = null;
            AssetItem? gameObjectItem = null;
            var selectedMeshes = new List<Mesh>();
            var seenMeshes = new HashSet<Mesh>();
            var selectedGameObjects = new List<GameObject>();
            var seenGameObjects = new HashSet<GameObject>();

            for (var i = 0; i < selectedAssets.Count; i++)
            {
                var item = selectedAssets[i];
                if (item == null)
                {
                    continue;
                }

                if (item.Type == ClassIDType.AnimationClip || item.Asset is AnimationClip)
                {
                    if (seenClipAssets.Add(item))
                    {
                        clipAssets.Add(item);
                    }
                    if (item.Asset is AnimationClip clip && seenClips.Add(clip))
                    {
                        resolvedClips.Add(clip);
                    }
                    continue;
                }

                if (animatorItem == null && (item.Type == ClassIDType.Animator || item.Asset is Animator))
                {
                    animatorItem = item;
                }

                if (avatarItem == null && (item.Type == ClassIDType.Avatar || item.Asset is Avatar))
                {
                    avatarItem = item;
                }

                if (smrItem == null && (item.Type == ClassIDType.SkinnedMeshRenderer || item.Asset is SkinnedMeshRenderer))
                {
                    smrItem = item;
                }

                if (item.Type == ClassIDType.Mesh || item.Asset is Mesh)
                {
                    firstMeshItem ??= item;
                    if (item.Asset is Mesh mesh && seenMeshes.Add(mesh))
                    {
                        selectedMeshes.Add(mesh);
                    }
                }

                if (gameObjectItem == null && (item.Type == ClassIDType.GameObject || item.Asset is GameObject))
                {
                    gameObjectItem = item;
                }

                if (item.Asset is GameObject directGo && seenGameObjects.Add(directGo))
                {
                    selectedGameObjects.Add(directGo);
                }
                else if (item.TreeNode?.GameObject != null && seenGameObjects.Add(item.TreeNode.GameObject))
                {
                    selectedGameObjects.Add(item.TreeNode.GameObject);
                    gameObjectItem ??= item;
                }
            }

            if (clipAssets.Count == 0 && resolvedClips.Count == 0)
            {
                return new BatchAnimationSelectionResolution { Kind = BatchAnimationSelectionKind.None };
            }

            var resolvedAvatar = avatarItem?.Asset as Avatar;
            var resolvedAnimator = animatorItem?.Asset as Animator;
            if (resolvedAvatar == null && resolvedAnimator?.m_Avatar != null)
            {
                resolvedAnimator.m_Avatar.TryGet(out resolvedAvatar);
            }

            var resolvedSmr = smrItem?.Asset as SkinnedMeshRenderer;
            Mesh? smrMesh = null;
            GameObject? smrGo = null;
            if (resolvedSmr != null)
            {
                resolvedSmr.m_Mesh?.TryGet(out smrMesh);
                resolvedSmr.m_GameObject?.TryGet(out smrGo);
                if (smrMesh != null && seenMeshes.Add(smrMesh))
                {
                    selectedMeshes.Add(smrMesh);
                }
                if (smrGo != null && seenGameObjects.Add(smrGo))
                {
                    selectedGameObjects.Add(smrGo);
                }
            }

            BatchAnimationSelectionKind kind;
            AssetItem? primaryModelAsset;

            if (animatorItem != null)
            {
                kind = BatchAnimationSelectionKind.ExplicitAnimator;
                primaryModelAsset = animatorItem;
            }
            else if (avatarItem != null)
            {
                kind = BatchAnimationSelectionKind.ExplicitAvatar;
                primaryModelAsset = avatarItem;
            }
            else if (smrItem != null)
            {
                kind = BatchAnimationSelectionKind.ExplicitSkinnedMeshRenderer;
                primaryModelAsset = smrItem;
            }
            else if (firstMeshItem != null)
            {
                kind = BatchAnimationSelectionKind.ExplicitMesh;
                primaryModelAsset = firstMeshItem;
            }
            else if (gameObjectItem != null || selectedGameObjects.Count > 0)
            {
                kind = BatchAnimationSelectionKind.ExplicitGameObject;
                primaryModelAsset = gameObjectItem;
            }
            else
            {
                kind = BatchAnimationSelectionKind.ClipsOnly;
                primaryModelAsset = null;
            }

            var primaryMesh = (firstMeshItem?.Asset as Mesh) ?? smrMesh ?? selectedMeshes.FirstOrDefault();
            var rootGo = (gameObjectItem?.Asset as GameObject)
                ?? gameObjectItem?.TreeNode?.GameObject
                ?? smrGo
                ?? selectedGameObjects.FirstOrDefault();

            var suggestedBaseName = SelectSuggestedBaseName(
                primaryModelAsset,
                resolvedAnimator,
                resolvedAvatar,
                primaryMesh,
                resolvedSmr,
                rootGo,
                clipAssets,
                resolvedClips);

            return new BatchAnimationSelectionResolution
            {
                Kind = kind,
                PrimaryModelAsset = primaryModelAsset,
                Animator = resolvedAnimator,
                Avatar = resolvedAvatar,
                PrimaryMesh = primaryMesh,
                SelectedMeshes = selectedMeshes,
                SkinnedMeshRenderer = resolvedSmr,
                RootGameObject = rootGo,
                SelectedGameObjects = selectedGameObjects,
                SelectedClipAssets = clipAssets,
                SelectedClips = resolvedClips,
                SuggestedBaseName = suggestedBaseName
            };
        }

        public static string SelectSuggestedBaseName(
            AssetItem? primaryModelAsset,
            Animator? animator,
            Avatar? avatar,
            Mesh? primaryMesh,
            SkinnedMeshRenderer? smr,
            GameObject? rootGo,
            IReadOnlyList<AssetItem> clipAssets,
            IReadOnlyList<AnimationClip> clips)
        {
            if (primaryModelAsset != null)
            {
                var fromAsset = AssetExportService.GetExportFileName(primaryModelAsset);
                if (!string.IsNullOrWhiteSpace(fromAsset))
                {
                    return AssetExportService.FixFileName(fromAsset);
                }
            }

            if (rootGo != null && !string.IsNullOrWhiteSpace(rootGo.m_Name))
            {
                return AssetExportService.FixFileName(rootGo.m_Name);
            }

            if (animator != null && animator.m_GameObject != null && animator.m_GameObject.TryGet(out var animGo) && !string.IsNullOrWhiteSpace(animGo?.m_Name))
            {
                return AssetExportService.FixFileName(animGo!.m_Name);
            }

            if (primaryMesh != null && !string.IsNullOrWhiteSpace(primaryMesh.m_Name))
            {
                return AssetExportService.FixFileName(primaryMesh.m_Name);
            }

            if (avatar != null && !string.IsNullOrWhiteSpace(avatar.m_Name))
            {
                return AssetExportService.FixFileName(avatar.m_Name);
            }

            if (smr?.m_GameObject != null && smr.m_GameObject.TryGet(out var smrOwnerGo) && !string.IsNullOrWhiteSpace(smrOwnerGo?.m_Name))
            {
                return AssetExportService.FixFileName(smrOwnerGo!.m_Name);
            }

            var firstClipName = clips.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c?.m_Name))?.m_Name
                ?? clipAssets.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c?.Name))?.Name;
            if (!string.IsNullOrWhiteSpace(firstClipName))
            {
                var prefix = TryExtractCharacterPrefixFromClipName(firstClipName!);
                return AssetExportService.FixFileName(!string.IsNullOrWhiteSpace(prefix) ? prefix : firstClipName);
            }

            return "Model";
        }

        public static string TryExtractCharacterPrefixFromClipName(string clipName)
        {
            if (string.IsNullOrWhiteSpace(clipName))
            {
                return "Model";
            }

            var trimmed = clipName.Trim();
            var atIndex = trimmed.IndexOf('@');
            if (atIndex > 1)
            {
                return trimmed.Substring(0, atIndex).Trim();
            }

            var underscoreIndex = trimmed.IndexOf('_');
            if (underscoreIndex > 1)
            {
                return trimmed.Substring(0, underscoreIndex).Trim();
            }

            return trimmed;
        }

        public static PreviewCandidateItem? SelectSharedPreviewCandidateForClips(
            IReadOnlyList<AnimationClip> clips,
            Func<AnimationClip, IReadOnlyList<PreviewCandidateItem>> candidateProvider,
            Func<AnimationClip, Avatar, bool>? isCompatibleWithAvatar = null,
            Func<AnimationClip, Mesh, bool>? isCompatibleWithMesh = null)
        {
            if (clips == null || clips.Count == 0 || candidateProvider == null)
            {
                return null;
            }

            var maxClipsToSample = Math.Min(clips.Count, 8);
            var candidateScores = new Dictionary<string, (PreviewCandidateItem BestCandidate, int ClipMatchCount, int TierScore, int Confidence)>(StringComparer.Ordinal);
            PreviewCandidateItem? firstBestFallback = null;

            for (var i = 0; i < maxClipsToSample; i++)
            {
                var clip = clips[i];
                if (clip == null)
                {
                    continue;
                }

                var candidates = candidateProvider(clip);
                if (candidates == null || candidates.Count == 0)
                {
                    continue;
                }

                firstBestFallback ??= MainWindow.SelectBestAnimationClipCandidate(
                    clip,
                    candidates,
                    preferredAvatar: null,
                    preferredMesh: null,
                    preferredAvatarId: null,
                    preferredMeshId: null,
                    isCompatibleWithAvatar,
                    isCompatibleWithMesh);

                var seenKeysForClip = new HashSet<string>(StringComparer.Ordinal);
                for (var c = 0; c < candidates.Count; c++)
                {
                    var candidate = candidates[c];
                    if (candidate == null)
                    {
                        continue;
                    }

                    if (candidate.IsCurrentPreviewFallback && !IsCurrentFallbackCompatible(clip, candidate, isCompatibleWithAvatar, isCompatibleWithMesh))
                    {
                        continue;
                    }

                    var key = BuildCandidateIdentityKey(candidate);
                    if (string.IsNullOrWhiteSpace(key) || !seenKeysForClip.Add(key))
                    {
                        continue;
                    }

                    var tier = ComputeCandidateTierScore(candidate);
                    var confidence = candidate.ModelGroupConfidence;

                    if (candidateScores.TryGetValue(key, out var existing))
                    {
                        var best = tier > existing.TierScore || (tier == existing.TierScore && confidence > existing.Confidence)
                            ? candidate
                            : existing.BestCandidate;
                        candidateScores[key] = (
                            best,
                            existing.ClipMatchCount + 1,
                            Math.Max(existing.TierScore, tier),
                            Math.Max(existing.Confidence, confidence));
                    }
                    else
                    {
                        candidateScores[key] = (candidate, 1, tier, confidence);
                    }
                }
            }

            if (candidateScores.Count == 0)
            {
                return firstBestFallback;
            }

            return candidateScores.Values
                .OrderByDescending(x => !x.BestCandidate.IsCurrentPreviewFallback)
                .ThenByDescending(x => x.ClipMatchCount)
                .ThenByDescending(x => x.TierScore)
                .ThenByDescending(x => x.Confidence)
                .ThenByDescending(x => Math.Max(x.BestCandidate.ModelGroupMeshCount, x.BestCandidate.ModelGroupMeshIds.Count))
                .Select(x => x.BestCandidate)
                .FirstOrDefault() ?? firstBestFallback;
        }

        private static bool IsCurrentFallbackCompatible(
            AnimationClip clip,
            PreviewCandidateItem candidate,
            Func<AnimationClip, Avatar, bool>? isCompatibleWithAvatar,
            Func<AnimationClip, Mesh, bool>? isCompatibleWithMesh)
        {
            if (candidate.Avatar != null && isCompatibleWithAvatar != null)
            {
                return isCompatibleWithAvatar(clip, candidate.Avatar);
            }

            if (candidate.Mesh != null && isCompatibleWithMesh != null)
            {
                return isCompatibleWithMesh(clip, candidate.Mesh);
            }

            return !candidate.IsCurrentPreviewFallback;
        }

        private static string BuildCandidateIdentityKey(PreviewCandidateItem candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate.ModelGroupId))
            {
                return "group:" + candidate.ModelGroupId;
            }

            var avatarKey = !string.IsNullOrWhiteSpace(candidate.AvatarId)
                ? candidate.AvatarId
                : candidate.Avatar != null
                    ? $"avatar:{candidate.Avatar.m_PathID.ToString(CultureInfo.InvariantCulture)}"
                    : string.Empty;
            var meshKey = !string.IsNullOrWhiteSpace(candidate.MeshId)
                ? candidate.MeshId
                : candidate.Mesh != null
                    ? $"mesh:{candidate.Mesh.m_PathID.ToString(CultureInfo.InvariantCulture)}"
                    : string.Empty;

            if (!string.IsNullOrEmpty(avatarKey) || !string.IsNullOrEmpty(meshKey))
            {
                return $"{avatarKey}|{meshKey}";
            }

            return candidate.Label ?? string.Empty;
        }

        private static int ComputeCandidateTierScore(PreviewCandidateItem candidate)
        {
            var hasAvatar = candidate.Avatar != null || !string.IsNullOrWhiteSpace(candidate.AvatarId);
            var hasMesh = candidate.Mesh != null
                || !string.IsNullOrWhiteSpace(candidate.MeshId)
                || candidate.ModelGroupMeshes.Count > 0
                || candidate.ModelGroupMeshInfos.Count > 0
                || candidate.ModelGroupMeshIds.Count > 0;

            if (candidate.IsModelGroup && hasAvatar && hasMesh) return 600;
            if (hasAvatar && hasMesh) return 500;
            if (candidate.IsModelGroup && hasMesh) return 400;
            if (hasMesh) return 300;
            if (hasAvatar) return 200;
            return 100;
        }

        public static void ExtractClipTimingMetadata(
            AnimationClip? clip,
            out float durationSeconds,
            out float sampleRate,
            out int frameCount)
        {
            durationSeconds = 0f;
            sampleRate = 30f;
            frameCount = 0;

            if (clip == null)
            {
                return;
            }

            if (clip.m_SampleRate > 0f && float.IsFinite(clip.m_SampleRate))
            {
                sampleRate = clip.m_SampleRate;
            }

            if (clip.m_MuscleClip != null)
            {
                var muscleDur = clip.m_MuscleClip.m_StopTime - clip.m_MuscleClip.m_StartTime;
                if (muscleDur > 0f && float.IsFinite(muscleDur))
                {
                    durationSeconds = muscleDur;
                }
                else if (clip.m_MuscleClip.m_StopTime > 0f && float.IsFinite(clip.m_MuscleClip.m_StopTime))
                {
                    durationSeconds = clip.m_MuscleClip.m_StopTime;
                }

                var dense = clip.m_MuscleClip.m_Clip?.m_DenseClip;
                if (dense != null && dense.m_FrameCount > 0)
                {
                    frameCount = dense.m_FrameCount;
                    if (durationSeconds <= 0f)
                    {
                        var denseRate = dense.m_SampleRate > 0f && float.IsFinite(dense.m_SampleRate)
                            ? dense.m_SampleRate
                            : sampleRate;
                        durationSeconds = Math.Max(0f, (dense.m_FrameCount - 1) / denseRate);
                    }
                }
            }

            if (durationSeconds <= 0f)
            {
                var maxTime = 0f;

                if (clip.m_RotationCurves != null)
                {
                    for (var i = 0; i < clip.m_RotationCurves.Length; i++)
                    {
                        var keys = clip.m_RotationCurves[i]?.curve?.m_Curve;
                        if (keys is { Length: > 0 })
                        {
                            var lastTime = keys[keys.Length - 1].time;
                            if (float.IsFinite(lastTime) && lastTime > maxTime) maxTime = lastTime;
                        }
                    }
                }

                if (clip.m_PositionCurves != null)
                {
                    for (var i = 0; i < clip.m_PositionCurves.Length; i++)
                    {
                        var keys = clip.m_PositionCurves[i]?.curve?.m_Curve;
                        if (keys is { Length: > 0 })
                        {
                            var lastTime = keys[keys.Length - 1].time;
                            if (float.IsFinite(lastTime) && lastTime > maxTime) maxTime = lastTime;
                        }
                    }
                }

                if (clip.m_ScaleCurves != null)
                {
                    for (var i = 0; i < clip.m_ScaleCurves.Length; i++)
                    {
                        var keys = clip.m_ScaleCurves[i]?.curve?.m_Curve;
                        if (keys is { Length: > 0 })
                        {
                            var lastTime = keys[keys.Length - 1].time;
                            if (float.IsFinite(lastTime) && lastTime > maxTime) maxTime = lastTime;
                        }
                    }
                }

                if (clip.m_EulerCurves != null)
                {
                    for (var i = 0; i < clip.m_EulerCurves.Length; i++)
                    {
                        var keys = clip.m_EulerCurves[i]?.curve?.m_Curve;
                        if (keys is { Length: > 0 })
                        {
                            var lastTime = keys[keys.Length - 1].time;
                            if (float.IsFinite(lastTime) && lastTime > maxTime) maxTime = lastTime;
                        }
                    }
                }

                if (clip.m_FloatCurves != null)
                {
                    for (var i = 0; i < clip.m_FloatCurves.Length; i++)
                    {
                        var keys = clip.m_FloatCurves[i]?.curve?.m_Curve;
                        if (keys is { Length: > 0 })
                        {
                            var lastTime = keys[keys.Length - 1].time;
                            if (float.IsFinite(lastTime) && lastTime > maxTime) maxTime = lastTime;
                        }
                    }
                }

                if (maxTime > 0f)
                {
                    durationSeconds = maxTime;
                }
            }

            if (frameCount <= 0 && durationSeconds > 0f && sampleRate > 0f)
            {
                frameCount = Math.Max(1, (int)Math.Round(durationSeconds * sampleRate, MidpointRounding.AwayFromZero) + 1);
            }
        }

        public static string FormatClipTimingSummary(float durationSeconds, float sampleRate, int frameCount)
        {
            if (durationSeconds <= 0f && frameCount <= 0)
            {
                return string.Empty;
            }

            var parts = new List<string>(3);
            if (durationSeconds > 0f && float.IsFinite(durationSeconds))
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{durationSeconds:0.##}s"));
            }
            if (frameCount > 0)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{frameCount}f"));
            }
            if (sampleRate > 0f && float.IsFinite(sampleRate))
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{sampleRate:0.#}fps"));
            }

            return string.Join(" • ", parts);
        }
    }
}
