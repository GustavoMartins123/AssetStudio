using System;
using System.Collections.Generic;
using System.Linq;
using AssetStudio;

namespace AssetStudio.Avalonia.Services;

internal sealed class AnimationTrackSet
{
    public Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>> PosTracks { get; } = new();
    public Dictionary<int, List<(float time, global::OpenTK.Mathematics.Quaternion value)>> RotTracks { get; } = new();
    public Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>> ScaleTracks { get; } = new();
    public float MaxTime { get; private set; }

    public int TotalTrackCount => PosTracks.Count + RotTracks.Count + ScaleTracks.Count;

    public void AddPosition(int boneIdx, float time, global::OpenTK.Mathematics.Vector3 value)
    {
        if (time > MaxTime)
        {
            MaxTime = time;
        }

        if (!PosTracks.TryGetValue(boneIdx, out var list))
        {
            PosTracks[boneIdx] = list = new();
        }

        list.Add((time, value));
    }

    public void AddRotation(int boneIdx, float time, global::OpenTK.Mathematics.Quaternion value)
    {
        if (value.LengthSquared > 0)
        {
            value.Normalize();
        }

        if (time > MaxTime)
        {
            MaxTime = time;
        }

        if (!RotTracks.TryGetValue(boneIdx, out var list))
        {
            RotTracks[boneIdx] = list = new();
        }

        list.Add((time, value));
    }

    public void AddScale(int boneIdx, float time, global::OpenTK.Mathematics.Vector3 value)
    {
        if (time > MaxTime)
        {
            MaxTime = time;
        }

        if (!ScaleTracks.TryGetValue(boneIdx, out var list))
        {
            ScaleTracks[boneIdx] = list = new();
        }

        list.Add((time, value));
    }

    public void AddGenericKeyframe(
        int meshBoneIdx,
        uint attribute,
        float time,
        float[] data,
        int offset,
        bool updateMaxTimeBeforeBoundsCheck = false)
    {
        if (updateMaxTimeBeforeBoundsCheck && time > MaxTime)
        {
            MaxTime = time;
        }

        if (offset < 0 || offset >= data.Length)
        {
            return;
        }

        switch (attribute)
        {
            case 1 when offset + 2 < data.Length:
                AddPosition(meshBoneIdx, time, new global::OpenTK.Mathematics.Vector3(data[offset], data[offset + 1], data[offset + 2]));
                break;
            case 2 when offset + 3 < data.Length:
                AddRotation(meshBoneIdx, time, new global::OpenTK.Mathematics.Quaternion(data[offset], data[offset + 1], data[offset + 2], data[offset + 3]));
                break;
            case 3 when offset + 2 < data.Length:
                AddScale(meshBoneIdx, time, new global::OpenTK.Mathematics.Vector3(data[offset], data[offset + 1], data[offset + 2]));
                break;
            case 4 when offset + 2 < data.Length:
                var unityQuat = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(data[offset], data[offset + 1], data[offset + 2], rotationOrder: 4);
                AddRotation(meshBoneIdx, time, MeshPreviewSkeletonBuilder.ToOtkQuaternion(unityQuat));
                break;
        }
    }

    public void SortAll()
    {
        foreach (var track in PosTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }

        foreach (var track in RotTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }

        foreach (var track in ScaleTracks.Values)
        {
            track.Sort((a, b) => a.time.CompareTo(b.time));
        }
    }
}

internal delegate bool TryResolveAnimationBoneDelegate(GenericBinding binding, out int meshBoneIdx);
internal delegate bool TryResolveCurvePathDelegate(string? path, out int meshBoneIdx);

/// <summary>
/// Pure stateless animation curve extraction and forward-kinematics frame evaluation
/// service for 3D Avatar and SkinnedMeshRenderer animation previews.
/// </summary>
internal static class MeshPreviewAnimationEvaluator
{
    public static void ExtractLegacyCurves(
        AnimationClip clip,
        TryResolveCurvePathDelegate tryResolveCurvePath,
        Func<int, bool> shouldUseBone,
        AnimationTrackSet tracks)
    {
        foreach (var curve in clip.m_PositionCurves ?? Array.Empty<Vector3Curve>())
        {
            if (!tryResolveCurvePath(curve.path, out var boneIdx) || !shouldUseBone(boneIdx) || curve.curve?.m_Curve == null)
            {
                continue;
            }

            foreach (var key in curve.curve.m_Curve)
            {
                tracks.AddPosition(boneIdx, key.time, MeshPreviewSkeletonBuilder.ToOtkVector3(key.value));
            }
        }

        foreach (var curve in clip.m_RotationCurves ?? Array.Empty<QuaternionCurve>())
        {
            if (!tryResolveCurvePath(curve.path, out var boneIdx) || !shouldUseBone(boneIdx) || curve.curve?.m_Curve == null)
            {
                continue;
            }

            foreach (var key in curve.curve.m_Curve)
            {
                tracks.AddRotation(boneIdx, key.time, MeshPreviewSkeletonBuilder.ToOtkQuaternion(key.value));
            }
        }

        foreach (var curve in clip.m_CompressedRotationCurves ?? Array.Empty<CompressedAnimationCurve>())
        {
            if (curve == null
                || !tryResolveCurvePath(curve.m_Path, out var boneIdx)
                || !shouldUseBone(boneIdx)
                || curve.m_Times == null
                || curve.m_Values == null)
            {
                continue;
            }

            int[] timeDeltas;
            AssetStudio.Quaternion[] quats;
            try
            {
                timeDeltas = curve.m_Times.UnpackInts();
                quats = curve.m_Values.UnpackQuats();
            }
            catch
            {
                continue;
            }

            var numKeys = Math.Min(timeDeltas.Length, quats.Length);
            var cumulativeTime = 0;
            for (var i = 0; i < numKeys; i++)
            {
                cumulativeTime += timeDeltas[i];
                var time = cumulativeTime * 0.01f;
                tracks.AddRotation(boneIdx, time, MeshPreviewSkeletonBuilder.ToOtkQuaternion(quats[i]));
            }
        }

        foreach (var curve in clip.m_EulerCurves ?? Array.Empty<Vector3Curve>())
        {
            if (!tryResolveCurvePath(curve.path, out var boneIdx) || !shouldUseBone(boneIdx) || curve.curve?.m_Curve == null)
            {
                continue;
            }

            var rotOrder = curve.curve.m_RotationOrder;
            foreach (var key in curve.curve.m_Curve)
            {
                var unityQuat = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(key.value.X, key.value.Y, key.value.Z, rotOrder);
                tracks.AddRotation(boneIdx, key.time, MeshPreviewSkeletonBuilder.ToOtkQuaternion(unityQuat));
            }
        }

        foreach (var curve in clip.m_ScaleCurves ?? Array.Empty<Vector3Curve>())
        {
            if (!tryResolveCurvePath(curve.path, out var boneIdx) || !shouldUseBone(boneIdx) || curve.curve?.m_Curve == null)
            {
                continue;
            }

            foreach (var key in curve.curve.m_Curve)
            {
                tracks.AddScale(boneIdx, key.time, MeshPreviewSkeletonBuilder.ToOtkVector3(key.value));
            }
        }
    }

    public static void ExtractMuscleClipTracks(
        Clip innerClip,
        AnimationClipBindingConstant bindings,
        float? constantStopTime,
        TryResolveAnimationBoneDelegate tryResolveBone,
        Func<int, bool> shouldUseBone,
        AnimationTrackSet tracks,
        bool updateMaxTimeBeforeBoundsCheck = false)
    {
        if (bindings.genericBindings == null || bindings.genericBindings.Length == 0)
        {
            return;
        }

        static int GetBindingCurveWidth(GenericBinding binding)
        {
            if (binding.typeID != ClassIDType.Transform)
            {
                return 1;
            }

            return binding.attribute switch
            {
                1 => 3,
                2 => 4,
                3 => 3,
                4 => 3,
                _ => 1
            };
        }

        // Pre-resolve every GenericBinding ONCE per clip instead of scanning genericBindings and avatar.m_TOS on every frame.
        var bindingCount = bindings.genericBindings.Length;
        var bindingStartIndices = new int[bindingCount];
        var bindingWidths = new int[bindingCount];
        var bindingMeshBones = new int[bindingCount];
        var bindingAttributes = new uint[bindingCount];
        var bindingIsValidTransform = new bool[bindingCount];
        var bindingIsTransform = new bool[bindingCount];

        var totalCurveSlots = 0;
        var hasAnyValidTransformBinding = false;
        for (var i = 0; i < bindingCount; i++)
        {
            var candidate = bindings.genericBindings[i];
            var width = GetBindingCurveWidth(candidate);
            bindingStartIndices[i] = totalCurveSlots;
            bindingWidths[i] = width;
            bindingAttributes[i] = candidate.attribute;
            bindingIsTransform[i] = candidate.typeID == ClassIDType.Transform;

            if (candidate.typeID == ClassIDType.Transform
                && tryResolveBone(candidate, out var meshBoneIdx)
                && shouldUseBone(meshBoneIdx))
            {
                bindingMeshBones[i] = meshBoneIdx;
                bindingIsValidTransform[i] = candidate.attribute is 1 or 2 or 3 or 4;
                if (bindingIsValidTransform[i])
                {
                    hasAnyValidTransformBinding = true;
                }
            }
            else
            {
                bindingMeshBones[i] = -1;
                bindingIsValidTransform[i] = false;
            }

            totalCurveSlots += width;
        }

        if (!hasAnyValidTransformBinding || totalCurveSlots <= 0)
        {
            return;
        }

        int[]? curveToBindingIndex = null;
        if (totalCurveSlots <= 131_072)
        {
            curveToBindingIndex = new int[totalCurveSlots];
            for (var b = 0; b < bindingCount; b++)
            {
                var start = bindingStartIndices[b];
                var width = bindingWidths[b];
                for (var c = 0; c < width; c++)
                {
                    curveToBindingIndex[start + c] = b;
                }
            }
        }

        bool TryFindBindingIndex(int curveIndex, out int bindingIdx)
        {
            if ((uint)curveIndex >= (uint)totalCurveSlots)
            {
                bindingIdx = -1;
                return false;
            }

            if (curveToBindingIndex != null)
            {
                bindingIdx = curveToBindingIndex[curveIndex];
                return true;
            }

            int lo = 0;
            int hi = bindingCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                int start = bindingStartIndices[mid];
                int width = bindingWidths[mid];
                if (curveIndex < start)
                {
                    hi = mid - 1;
                }
                else if (curveIndex >= start + width)
                {
                    lo = mid + 1;
                }
                else
                {
                    bindingIdx = mid;
                    return true;
                }
            }

            bindingIdx = -1;
            return false;
        }

        void ProcessCurveData(int curveIndexInStream, float time, float[] data, int dataOffset, ref int currentIdxOut)
        {
            if (!TryFindBindingIndex(curveIndexInStream, out var bIdx)
                || curveIndexInStream != bindingStartIndices[bIdx])
            {
                currentIdxOut++;
                return;
            }

            if (bindingIsTransform[bIdx])
            {
                var width = bindingWidths[bIdx];
                if (bindingIsValidTransform[bIdx])
                {
                    tracks.AddGenericKeyframe(
                        bindingMeshBones[bIdx],
                        bindingAttributes[bIdx],
                        time,
                        data,
                        currentIdxOut + dataOffset,
                        updateMaxTimeBeforeBoundsCheck);
                    currentIdxOut += width;
                }
                else if (bindingMeshBones[bIdx] >= 0)
                {
                    currentIdxOut++;
                }
                else
                {
                    currentIdxOut += width;
                }
            }
            else
            {
                currentIdxOut++;
            }
        }

        if (innerClip.m_StreamedClip != null
            && innerClip.m_StreamedClip.curveCount > 0
            && innerClip.m_StreamedClip.data != null
            && innerClip.m_StreamedClip.data.Length > 0)
        {
            List<StreamedClip.StreamedFrame>? streamedFrames = null;
            try
            {
                streamedFrames = innerClip.m_StreamedClip.ReadData();
            }
            catch
            {
                streamedFrames = null;
            }

            if (streamedFrames != null && streamedFrames.Count >= 3)
            {
                var streamedEval = new HumanoidMuscleDecoder.StreamedCurveEvaluator(streamedFrames);
                if (streamedEval.HasAnyKeys)
                {
                    var streamLimit = (int)Math.Min(innerClip.m_StreamedClip.curveCount, (uint)totalCurveSlots);
                    var tempValues = new float[4];
                    var bindingTimes = new List<float>();
                    const float dtStep = 1f / 30f;

                    for (var bIdx = 0; bIdx < bindingCount; bIdx++)
                    {
                        var start = bindingStartIndices[bIdx];
                        if (start >= streamLimit)
                        {
                            break;
                        }

                        if (!bindingIsValidTransform[bIdx])
                        {
                            continue;
                        }

                        var width = bindingWidths[bIdx];
                        var attr = bindingAttributes[bIdx];
                        var boneIdx = bindingMeshBones[bIdx];
                        bindingTimes.Clear();
                        for (var c = 0; c < width && start + c < streamLimit; c++)
                        {
                            if (streamedEval.TryGetCurveKeys(start + c, out var keys) && keys.Count > 0)
                            {
                                for (var k = 0; k < keys.Count; k++)
                                {
                                    bindingTimes.Add(keys[k].Time);
                                }
                            }
                        }

                        if (bindingTimes.Count == 0)
                        {
                            continue;
                        }

                        bindingTimes.Sort();
                        var uniqueCount = 0;
                        for (var i = 0; i < bindingTimes.Count; i++)
                        {
                            if (uniqueCount == 0 || bindingTimes[i] - bindingTimes[uniqueCount - 1] > 1e-6f)
                            {
                                bindingTimes[uniqueCount++] = bindingTimes[i];
                            }
                        }

                        var curvatureTol = attr == 4 ? 0.05f : 1e-3f;
                        void EmitStreamedSample(float sampleTime)
                        {
                            for (var c = 0; c < width; c++)
                            {
                                var fallback = attr == 3
                                    ? 1f
                                    : (attr == 2 && c == 3 ? 1f : 0f);
                                tempValues[c] = start + c < streamLimit
                                    ? streamedEval.EvaluateOrDefault(start + c, sampleTime, fallback)
                                    : fallback;
                            }

                            tracks.AddGenericKeyframe(
                                boneIdx,
                                attr,
                                sampleTime,
                                tempValues,
                                0,
                                updateMaxTimeBeforeBoundsCheck);
                        }

                        for (var i = 0; i < uniqueCount; i++)
                        {
                            var t0 = bindingTimes[i];
                            var segStepped = true;
                            for (var c = 0; c < width && start + c < streamLimit; c++)
                            {
                                if (streamedEval.TryEvaluate(start + c, t0, out _, out var compStepped) && !compStepped)
                                {
                                    segStepped = false;
                                    break;
                                }
                            }

                            EmitStreamedSample(t0);

                            if (!segStepped && i + 1 < uniqueCount)
                            {
                                var t1 = bindingTimes[i + 1];
                                var span = t1 - t0;
                                if (span > dtStep * 1.25f)
                                {
                                    var tMid = 0.5f * (t0 + t1);
                                    var needsSubdivision = false;
                                    for (var c = 0; c < width && start + c < streamLimit; c++)
                                    {
                                        var v0 = streamedEval.EvaluateOrDefault(start + c, t0, 0f);
                                        var v1 = streamedEval.EvaluateOrDefault(start + c, t1, 0f);
                                        var vMid = streamedEval.EvaluateOrDefault(start + c, tMid, 0f);
                                        if (MathF.Abs(vMid - 0.5f * (v0 + v1)) > curvatureTol)
                                        {
                                            needsSubdivision = true;
                                            break;
                                        }
                                    }

                                    if (needsSubdivision)
                                    {
                                        var subSteps = Math.Clamp((int)MathF.Ceiling(span / dtStep), 2, 120);
                                        for (var s = 1; s < subSteps; s++)
                                        {
                                            EmitStreamedSample(t0 + span * s / subSteps);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        if (innerClip.m_DenseClip != null && innerClip.m_DenseClip.m_FrameCount > 0 && innerClip.m_DenseClip.m_CurveCount > 0)
        {
            var dense = innerClip.m_DenseClip;
            var streamCount = innerClip.m_StreamedClip?.curveCount ?? 0;
            var denseSampleRate = dense.m_SampleRate > 0f && float.IsFinite(dense.m_SampleRate) ? dense.m_SampleRate : 30f;
            for (var frameIndex = 0; frameIndex < dense.m_FrameCount; frameIndex++)
            {
                var time = dense.m_BeginTime + frameIndex / denseSampleRate;
                var frameOffset = frameIndex * dense.m_CurveCount;
                for (var cIdx = 0; cIdx < dense.m_CurveCount;)
                {
                    ProcessCurveData((int)(streamCount + cIdx), time, dense.m_SampleArray, (int)frameOffset, ref cIdx);
                }
            }
        }

        if (innerClip.m_ConstantClip?.data != null && innerClip.m_ConstantClip.data.Length > 0)
        {
            var constant = innerClip.m_ConstantClip;
            var denseCount = innerClip.m_DenseClip?.m_CurveCount ?? 0;
            var streamCount = innerClip.m_StreamedClip?.curveCount ?? 0;
            var stopTime = constantStopTime is float s && float.IsFinite(s) && s > 1e-6f ? s : 0.0f;
            var passes = stopTime > 1e-6f ? 2 : 1;
            for (var i = 0; i < passes; i++)
            {
                var time = i == 0 ? 0.0f : stopTime;
                for (var cIdx = 0; cIdx < constant.data.Length;)
                {
                    ProcessCurveData((int)(streamCount + denseCount + cIdx), time, constant.data, 0, ref cIdx);
                }
            }
        }
    }

    public static int ExtractHumanoidMuscleTracks(
        AnimationClip clip,
        Avatar avatar,
        AvatarMeshSkeletonData skeleton,
        Dictionary<string, int> meshBonePathToIdx,
        Func<int, bool> shouldUseBone,
        AnimationTrackSet tracks,
        bool targetAvatarNodeIndices = false)
    {
        if (clip == null || avatar?.m_Avatar?.m_AvatarSkeleton?.m_ID == null || skeleton == null)
        {
            return 0;
        }

        var decodedTracks = HumanoidMuscleDecoder.DecodeHumanoidClip(clip, avatar);
        if (decodedTracks.Count == 0)
        {
            return 0;
        }

        var skelIds = avatar.m_Avatar.m_AvatarSkeleton.m_ID;
        Dictionary<int, int>? skelNodeToMeshBone = null;
        if (!targetAvatarNodeIndices)
        {
            skelNodeToMeshBone = new Dictionary<int, int>(skeleton.MeshBoneToSkelNode.Length);
            for (var mb = 0; mb < skeleton.MeshBoneToSkelNode.Length; mb++)
            {
                var sn = skeleton.MeshBoneToSkelNode[mb];
                if (sn >= 0)
                {
                    skelNodeToMeshBone[sn] = mb;
                }
            }
        }

        var addedTracks = 0;
        for (var i = 0; i < decodedTracks.Count; i++)
        {
            var decoded = decodedTracks[i];
            int targetIdx = -1;
            if (targetAvatarNodeIndices)
            {
                if (decoded.AvatarNodeIndex >= 0 && decoded.AvatarNodeIndex < skeleton.AvatarNodeCount)
                {
                    targetIdx = decoded.AvatarNodeIndex;
                }
                else if (decoded.AvatarNodeIndex >= 0 && decoded.AvatarNodeIndex < skelIds.Length
                    && skeleton.AvatarNodeHashToIdx.TryGetValue(skelIds[decoded.AvatarNodeIndex], out var byHash))
                {
                    targetIdx = byHash;
                }
                else if (!string.IsNullOrEmpty(decoded.BonePath)
                    && MeshPreviewSkeletonBuilder.TryResolvePathAlias(meshBonePathToIdx, decoded.BonePath, out var byPath))
                {
                    targetIdx = byPath;
                }
            }
            else if (!skelNodeToMeshBone!.TryGetValue(decoded.AvatarNodeIndex, out targetIdx))
            {
                if (decoded.AvatarNodeIndex >= 0 && decoded.AvatarNodeIndex < skelIds.Length
                    && skeleton.MeshBoneHashToIdx.TryGetValue(skelIds[decoded.AvatarNodeIndex], out var byHash))
                {
                    targetIdx = byHash;
                }
                else if (!string.IsNullOrEmpty(decoded.BonePath)
                    && MeshPreviewSkeletonBuilder.TryResolvePathAlias(meshBonePathToIdx, decoded.BonePath, out var byPath))
                {
                    targetIdx = byPath;
                }
            }

            if (targetIdx < 0 || !shouldUseBone(targetIdx))
            {
                continue;
            }

            bool addedAny = false;
            if (!tracks.PosTracks.ContainsKey(targetIdx) && decoded.Translations.Count > 0)
            {
                for (var k = 0; k < decoded.Translations.Count; k++)
                {
                    var kf = decoded.Translations[k];
                    tracks.AddPosition(targetIdx, kf.time, MeshPreviewSkeletonBuilder.ToOtkVector3(kf.value));
                }
                addedAny = true;
            }

            if (!tracks.RotTracks.ContainsKey(targetIdx) && decoded.Rotations.Count > 0)
            {
                for (var k = 0; k < decoded.Rotations.Count; k++)
                {
                    var kf = decoded.Rotations[k];
                    tracks.AddRotation(targetIdx, kf.time, MeshPreviewSkeletonBuilder.ToOtkQuaternion(kf.value));
                }
                addedAny = true;
            }

            if (addedAny)
            {
                addedTracks++;
            }
        }

        return addedTracks;
    }

    public static bool EvaluateFramesFromTracks(
        Mesh mesh,
        global::OpenTK.Mathematics.Vector3[] restBonePositions,
        int[] meshParentIndices,
        string[] meshBoneNames,
        global::OpenTK.Mathematics.Matrix4[] bindPoseInverses,
        bool[] deformChainMask,
        bool[] weightedBoneMask,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>> posTracks,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Quaternion value)>> rotTracks,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>> scaleTracks,
        float sampleRate,
        float maxTime,
        XForm[]? avatarDefaultPose,
        int[]? meshBoneToSkelNode,
        bool useCeilingFrameCount,
        bool failIfAllTracksFiltered,
        out global::OpenTK.Mathematics.Vector3[][] allFrames,
        out global::OpenTK.Mathematics.Matrix4[][] allBoneMatrices,
        out int[] renderParentIndices,
        out float resolvedSampleRate,
        out string reason)
    {
        allFrames = Array.Empty<global::OpenTK.Mathematics.Vector3[]>();
        allBoneMatrices = Array.Empty<global::OpenTK.Mathematics.Matrix4[]>();
        renderParentIndices = Array.Empty<int>();
        resolvedSampleRate = sampleRate;
        reason = string.Empty;

        var meshBoneCount = Math.Min(mesh.m_BindPose?.Length ?? 0, Math.Min(restBonePositions.Length, meshParentIndices.Length));
        if (meshBoneCount == 0)
        {
            reason = "No bones available for frame generation.";
            return false;
        }

        static int FindKeyframeSegmentIndex<T>(List<(float time, T value)> track, float t)
        {
            int lo = 0;
            int hi = track.Count - 2;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (track[mid + 1].time < t)
                {
                    lo = mid + 1;
                }
                else if (track[mid].time > t)
                {
                    hi = mid - 1;
                }
                else
                {
                    return mid;
                }
            }

            return Math.Clamp(lo, 0, track.Count - 2);
        }

        global::OpenTK.Mathematics.Vector3 EvaluatePos(int meshBoneIdx, float t)
        {
            if (!posTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.Zero;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            var idx = FindKeyframeSegmentIndex(track, t);
            var span = track[idx + 1].time - track[idx].time;
            var factor = span > 1e-6f ? (t - track[idx].time) / span : 0f;
            return global::OpenTK.Mathematics.Vector3.Lerp(track[idx].value, track[idx + 1].value, factor);
        }

        global::OpenTK.Mathematics.Quaternion EvaluateRot(int meshBoneIdx, float t)
        {
            if (!rotTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Quaternion.Identity;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            var idx = FindKeyframeSegmentIndex(track, t);
            var span = track[idx + 1].time - track[idx].time;
            var factor = span > 1e-6f ? (t - track[idx].time) / span : 0f;
            return global::OpenTK.Mathematics.Quaternion.Slerp(track[idx].value, track[idx + 1].value, factor);
        }

        global::OpenTK.Mathematics.Vector3 EvaluateScale(int meshBoneIdx, float t)
        {
            if (!scaleTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.One;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            var idx = FindKeyframeSegmentIndex(track, t);
            var span = track[idx + 1].time - track[idx].time;
            var factor = span > 1e-6f ? (t - track[idx].time) / span : 0f;
            return global::OpenTK.Mathematics.Vector3.Lerp(track[idx].value, track[idx + 1].value, factor);
        }

        var bindPoses = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        for (var i = 0; i < meshBoneCount; i++)
        {
            try { bindPoses[i] = bindPoseInverses[i].Inverted(); }
            catch { bindPoses[i] = global::OpenTK.Mathematics.Matrix4.Identity; }
        }

        var restLocals = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
        for (var i = 0; i < meshBoneCount; i++)
        {
            var skelIdx = meshBoneToSkelNode != null && i < meshBoneToSkelNode.Length
                ? meshBoneToSkelNode[i]
                : -1;
            if (avatarDefaultPose != null && skelIdx >= 0 && skelIdx < avatarDefaultPose.Length)
            {
                var xform = avatarDefaultPose[skelIdx];
                restLocals[i] = MeshPreviewSkeletonBuilder.CreateLocalMatrix(
                    MeshPreviewSkeletonBuilder.ToOtkVector3(xform.t),
                    MeshPreviewSkeletonBuilder.ToOtkQuaternion(xform.q),
                    MeshPreviewSkeletonBuilder.ToOtkVector3(xform.s));
            }
            else
            {
                var pIdx = meshParentIndices[i];
                restLocals[i] = pIdx >= 0 && pIdx < meshBoneCount
                    ? bindPoseInverses[i] * bindPoses[pIdx]
                    : bindPoseInverses[i];
            }
        }

        var restMin = new global::OpenTK.Mathematics.Vector3(float.MaxValue);
        var restMax = new global::OpenTK.Mathematics.Vector3(float.MinValue);
        for (var i = 0; i < meshBoneCount; i++)
        {
            var restPosition = restBonePositions[i];
            if (!MeshPreviewSkeletonBuilder.IsFiniteVector(restPosition))
            {
                continue;
            }

            restMin = global::OpenTK.Mathematics.Vector3.ComponentMin(restMin, restPosition);
            restMax = global::OpenTK.Mathematics.Vector3.ComponentMax(restMax, restPosition);
        }

        var restExtent = (restMax - restMin).Length;
        if (!float.IsFinite(restExtent) || restExtent <= 0)
        {
            restExtent = 1f;
        }

        var localPositionLimit = Math.Max(1f, restExtent * 2f);
        foreach (var item in posTracks.ToArray())
        {
            var boneIdx = item.Key;
            if (boneIdx < 0 || boneIdx >= meshBoneCount || meshParentIndices[boneIdx] < 0)
            {
                continue;
            }

            var restLocalPosition = restLocals[boneIdx].ExtractTranslation();
            if (item.Value.Any(x => !MeshPreviewSkeletonBuilder.IsFiniteVector(x.value) || (x.value - restLocalPosition).Length > localPositionLimit))
            {
                posTracks.Remove(boneIdx);
            }
        }

        foreach (var item in scaleTracks.ToArray())
        {
            if (item.Value.Any(x => !MeshPreviewSkeletonBuilder.IsFiniteVector(x.value)
                || x.value.X <= 0f || x.value.Y <= 0f || x.value.Z <= 0f
                || Math.Abs(x.value.X) > 10f || Math.Abs(x.value.Y) > 10f || Math.Abs(x.value.Z) > 10f))
            {
                scaleTracks.Remove(item.Key);
            }
        }

        if (failIfAllTracksFiltered && posTracks.Count == 0 && rotTracks.Count == 0 && scaleTracks.Count == 0)
        {
            reason = "All extracted tracks were rejected by safety filters.";
            return false;
        }

        if (sampleRate <= 0f || !float.IsFinite(sampleRate))
        {
            sampleRate = 30f;
        }

        resolvedSampleRate = sampleRate;
        if (maxTime <= 0f || !float.IsFinite(maxTime))
        {
            maxTime = 1f;
        }

        var frameCount = useCeilingFrameCount
            ? Math.Max(1, (int)Math.Ceiling(maxTime * sampleRate))
            : Math.Max(1, (int)(maxTime * sampleRate));

        allFrames = new global::OpenTK.Mathematics.Vector3[frameCount][];
        allBoneMatrices = new global::OpenTK.Mathematics.Matrix4[frameCount][];
        for (var f = 0; f < frameCount; f++)
        {
            var t = f / sampleRate;
            var framePositions = new global::OpenTK.Mathematics.Vector3[meshBoneCount];
            var frameMatrices = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
            var modelMatrices = new global::OpenTK.Mathematics.Matrix4?[meshBoneCount];

            global::OpenTK.Mathematics.Matrix4 GetModelMatrix(int bIdx)
            {
                if (modelMatrices[bIdx] is global::OpenTK.Mathematics.Matrix4 cached)
                {
                    return cached;
                }

                var localMat = restLocals[bIdx];
                var hasPos = posTracks.ContainsKey(bIdx);
                var hasRot = rotTracks.ContainsKey(bIdx);
                var hasScale = scaleTracks.ContainsKey(bIdx);

                if (hasPos || hasRot || hasScale)
                {
                    var pos = hasPos ? EvaluatePos(bIdx, t) : localMat.ExtractTranslation();
                    var rot = hasRot ? EvaluateRot(bIdx, t) : localMat.ExtractRotation();
                    var scale = hasScale ? EvaluateScale(bIdx, t) : localMat.ExtractScale();
                    localMat = MeshPreviewSkeletonBuilder.CreateLocalMatrix(pos, rot, scale);
                }

                var pIdx = meshParentIndices[bIdx];
                if (pIdx >= 0 && pIdx != bIdx && pIdx < meshBoneCount)
                {
                    var worldMat = localMat * GetModelMatrix(pIdx);
                    modelMatrices[bIdx] = worldMat;
                    return worldMat;
                }

                modelMatrices[bIdx] = localMat;
                return localMat;
            }

            for (var meshBoneIdx = 0; meshBoneIdx < meshBoneCount; meshBoneIdx++)
            {
                var mat = GetModelMatrix(meshBoneIdx);
                framePositions[meshBoneIdx] = mat.ExtractTranslation();
                frameMatrices[meshBoneIdx] = mat;
            }

            allFrames[f] = framePositions;
            allBoneMatrices[f] = frameMatrices;
        }

        renderParentIndices = meshParentIndices.Take(meshBoneCount).ToArray();
        var hiddenRenderBones = new bool[meshBoneCount];
        for (var i = 0; i < meshBoneCount; i++)
        {
            var boneName = i < meshBoneNames.Length ? meshBoneNames[i] : string.Empty;
            if (!deformChainMask[i] || (!weightedBoneMask[i] && MeshPreviewSkeletonBuilder.IsAuxiliaryAnimationBone(boneName)))
            {
                hiddenRenderBones[i] = true;
                renderParentIndices[i] = -1;
            }
        }

        var maxRestEdge = 0f;
        for (var i = 0; i < meshBoneCount; i++)
        {
            var parentIdx = meshParentIndices[i];
            if (parentIdx >= 0 && parentIdx < meshBoneCount
                && MeshPreviewSkeletonBuilder.IsFiniteVector(restBonePositions[i])
                && MeshPreviewSkeletonBuilder.IsFiniteVector(restBonePositions[parentIdx]))
            {
                maxRestEdge = Math.Max(maxRestEdge, (restBonePositions[i] - restBonePositions[parentIdx]).Length);
            }
        }

        var edgeLimit = Math.Max(Math.Max(maxRestEdge * 8f, restExtent * 1.5f), 0.5f);
        for (var i = 0; i < meshBoneCount; i++)
        {
            var parentIdx = meshParentIndices[i];
            if (parentIdx < 0 || parentIdx >= meshBoneCount)
            {
                continue;
            }

            foreach (var frame in allFrames)
            {
                if (!MeshPreviewSkeletonBuilder.IsFiniteVector(frame[i])
                    || !MeshPreviewSkeletonBuilder.IsFiniteVector(frame[parentIdx])
                    || (frame[i] - frame[parentIdx]).Length > edgeLimit)
                {
                    hiddenRenderBones[i] = true;
                    renderParentIndices[i] = -1;
                    break;
                }
            }
        }

        for (var f = 0; f < allFrames.Length; f++)
        {
            for (var i = 0; i < meshBoneCount; i++)
            {
                if (!hiddenRenderBones[i])
                {
                    continue;
                }

                var parentIdx = meshParentIndices[i];
                allFrames[f][i] = parentIdx >= 0 && parentIdx < meshBoneCount
                    ? allFrames[f][parentIdx]
                    : restBonePositions[i];
            }
        }

        return true;
    }

    /// <summary>
    /// Evaluates forward kinematics across the FULL Avatar skeleton hierarchy (including intermediate
    /// non-mesh parent nodes such as root rotation/scale containers and un-weighted parent bones)
    /// and projects the resulting world transforms onto each mesh bone for accurate skinning.
    /// </summary>
    public static bool EvaluateFramesFromAvatarSkeletonTracks(
        Mesh mesh,
        AvatarMeshSkeletonData skeleton,
        XForm[] avatarDefaultPose,
        bool[] deformChainMask,
        bool[] weightedBoneMask,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>> posTracks,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Quaternion value)>> rotTracks,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>> scaleTracks,
        float sampleRate,
        float maxTime,
        bool useCeilingFrameCount,
        bool failIfAllTracksFiltered,
        out global::OpenTK.Mathematics.Vector3[][] allFrames,
        out global::OpenTK.Mathematics.Matrix4[][] allBoneMatrices,
        out int[] renderParentIndices,
        out float resolvedSampleRate,
        out string reason)
    {
        allFrames = Array.Empty<global::OpenTK.Mathematics.Vector3[]>();
        allBoneMatrices = Array.Empty<global::OpenTK.Mathematics.Matrix4[]>();
        renderParentIndices = Array.Empty<int>();
        resolvedSampleRate = sampleRate;
        reason = string.Empty;

        var meshBoneCount = Math.Min(mesh.m_BindPose?.Length ?? 0, Math.Min(skeleton.MeshBoneCount, skeleton.MeshParentIndices.Length));
        var skelCount = skeleton.AvatarNodeCount;
        if (meshBoneCount == 0 || skelCount == 0 || avatarDefaultPose.Length == 0)
        {
            reason = "No Avatar skeleton nodes available for frame generation.";
            return false;
        }

        static int FindSegment<T>(List<(float time, T value)> track, float t)
        {
            int lo = 0;
            int hi = track.Count - 2;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (track[mid + 1].time < t)
                {
                    lo = mid + 1;
                }
                else if (track[mid].time > t)
                {
                    hi = mid - 1;
                }
                else
                {
                    return mid;
                }
            }

            return Math.Clamp(lo, 0, track.Count - 2);
        }

        global::OpenTK.Mathematics.Vector3 EvalPos(int nodeIdx, float t)
        {
            if (!posTracks.TryGetValue(nodeIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.Zero;
            if (track.Count == 1 || t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;
            var idx = FindSegment(track, t);
            var span = track[idx + 1].time - track[idx].time;
            var factor = span > 1e-6f ? (t - track[idx].time) / span : 0f;
            return global::OpenTK.Mathematics.Vector3.Lerp(track[idx].value, track[idx + 1].value, factor);
        }

        global::OpenTK.Mathematics.Quaternion EvalRot(int nodeIdx, float t)
        {
            if (!rotTracks.TryGetValue(nodeIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Quaternion.Identity;
            if (track.Count == 1 || t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;
            var idx = FindSegment(track, t);
            var span = track[idx + 1].time - track[idx].time;
            var factor = span > 1e-6f ? (t - track[idx].time) / span : 0f;
            return global::OpenTK.Mathematics.Quaternion.Slerp(track[idx].value, track[idx + 1].value, factor);
        }

        global::OpenTK.Mathematics.Vector3 EvalScale(int nodeIdx, float t)
        {
            if (!scaleTracks.TryGetValue(nodeIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.One;
            if (track.Count == 1 || t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;
            var idx = FindSegment(track, t);
            var span = track[idx + 1].time - track[idx].time;
            var factor = span > 1e-6f ? (t - track[idx].time) / span : 0f;
            return global::OpenTK.Mathematics.Vector3.Lerp(track[idx].value, track[idx + 1].value, factor);
        }

        var avatarParents = skeleton.AvatarParentIndices;
        var skelRestPos = new global::OpenTK.Mathematics.Vector3[skelCount];
        var skelRestRot = new global::OpenTK.Mathematics.Quaternion[skelCount];
        var skelRestScale = new global::OpenTK.Mathematics.Vector3[skelCount];
        var skelRestLocals = new global::OpenTK.Mathematics.Matrix4[skelCount];
        for (var s = 0; s < skelCount; s++)
        {
            if (s < avatarDefaultPose.Length && avatarDefaultPose[s] != null)
            {
                var xf = avatarDefaultPose[s];
                skelRestPos[s] = MeshPreviewSkeletonBuilder.ToOtkVector3(xf.t);
                skelRestRot[s] = MeshPreviewSkeletonBuilder.ToOtkQuaternion(xf.q);
                skelRestScale[s] = MeshPreviewSkeletonBuilder.ToOtkVector3(xf.s);
                skelRestLocals[s] = MeshPreviewSkeletonBuilder.CreateLocalMatrix(skelRestPos[s], skelRestRot[s], skelRestScale[s]);
            }
            else
            {
                skelRestPos[s] = global::OpenTK.Mathematics.Vector3.Zero;
                skelRestRot[s] = global::OpenTK.Mathematics.Quaternion.Identity;
                skelRestScale[s] = global::OpenTK.Mathematics.Vector3.One;
                skelRestLocals[s] = global::OpenTK.Mathematics.Matrix4.Identity;
            }
        }

        // Compute rest world transforms in Avatar space for accurate scale-matched safety & render edge thresholds.
        var skelRestWorlds = new global::OpenTK.Mathematics.Matrix4?[skelCount];
        var restVisiting = new bool[skelCount];
        global::OpenTK.Mathematics.Matrix4 GetSkelRestWorld(int s)
        {
            if (skelRestWorlds[s] is global::OpenTK.Mathematics.Matrix4 cached)
            {
                return cached;
            }

            if (restVisiting[s])
            {
                return skelRestLocals[s];
            }

            restVisiting[s] = true;
            var p = s < avatarParents.Length ? avatarParents[s] : -1;
            var world = p >= 0 && p < skelCount && p != s
                ? skelRestLocals[s] * GetSkelRestWorld(p)
                : skelRestLocals[s];
            restVisiting[s] = false;
            skelRestWorlds[s] = world;
            return world;
        }

        var avatarRestBonePositions = new global::OpenTK.Mathematics.Vector3[meshBoneCount];
        var restMin = new global::OpenTK.Mathematics.Vector3(float.MaxValue);
        var restMax = new global::OpenTK.Mathematics.Vector3(float.MinValue);
        var hasUnmappedMeshBone = false;
        for (var mb = 0; mb < meshBoneCount; mb++)
        {
            var skelIdx = mb < skeleton.MeshBoneToSkelNode.Length ? skeleton.MeshBoneToSkelNode[mb] : -1;
            global::OpenTK.Mathematics.Vector3 pos;
            if (skelIdx >= 0 && skelIdx < skelCount)
            {
                pos = GetSkelRestWorld(skelIdx).ExtractTranslation();
            }
            else
            {
                hasUnmappedMeshBone = true;
                pos = mb < skeleton.RestBonePositions.Length ? skeleton.RestBonePositions[mb] : global::OpenTK.Mathematics.Vector3.Zero;
            }

            avatarRestBonePositions[mb] = pos;
            if (MeshPreviewSkeletonBuilder.IsFiniteVector(pos))
            {
                restMin = global::OpenTK.Mathematics.Vector3.ComponentMin(restMin, pos);
                restMax = global::OpenTK.Mathematics.Vector3.ComponentMax(restMax, pos);
            }
        }

        var restExtent = float.IsFinite(restMin.X) && float.IsFinite(restMax.X)
            ? (restMax - restMin).Length
            : 1f;
        if (!float.IsFinite(restExtent) || restExtent <= 0f)
        {
            restExtent = 1f;
        }

        foreach (var item in posTracks.ToArray())
        {
            var nodeIdx = item.Key;
            if (nodeIdx < 0 || nodeIdx >= skelCount)
            {
                posTracks.Remove(nodeIdx);
                continue;
            }

            var p = nodeIdx < avatarParents.Length ? avatarParents[nodeIdx] : -1;
            if (p < 0 || p >= skelCount)
            {
                continue;
            }

            var parentWorld = GetSkelRestWorld(p);
            var sx = parentWorld.Row0.Xyz.Length;
            var sy = parentWorld.Row1.Xyz.Length;
            var sz = parentWorld.Row2.Xyz.Length;
            var minParentScale = Math.Min(sx, Math.Min(sy, sz));
            var safeParentScale = float.IsFinite(minParentScale) && minParentScale > 1e-4f ? minParentScale : 1f;
            var localPositionLimit = Math.Max(1f, (restExtent * 2f) / safeParentScale);

            var restLocalPosition = skelRestPos[nodeIdx];
            if (item.Value.Any(x => !MeshPreviewSkeletonBuilder.IsFiniteVector(x.value) || (x.value - restLocalPosition).Length > localPositionLimit))
            {
                posTracks.Remove(nodeIdx);
            }
        }

        foreach (var item in scaleTracks.ToArray())
        {
            var nodeIdx = item.Key;
            if (nodeIdx < 0 || nodeIdx >= skelCount)
            {
                scaleTracks.Remove(nodeIdx);
                continue;
            }

            var restScale = skelRestScale[nodeIdx];
            var restScaleMag = Math.Max(Math.Abs(restScale.X), Math.Max(Math.Abs(restScale.Y), Math.Abs(restScale.Z)));
            var maxScaleLimit = Math.Max(10f, float.IsFinite(restScaleMag) ? restScaleMag * 10f : 10f);
            if (item.Value.Any(x => !MeshPreviewSkeletonBuilder.IsFiniteVector(x.value)
                || x.value.X <= 0f || x.value.Y <= 0f || x.value.Z <= 0f
                || Math.Abs(x.value.X) > maxScaleLimit || Math.Abs(x.value.Y) > maxScaleLimit || Math.Abs(x.value.Z) > maxScaleLimit))
            {
                scaleTracks.Remove(nodeIdx);
            }
        }

        if (failIfAllTracksFiltered && posTracks.Count == 0 && rotTracks.Count == 0 && scaleTracks.Count == 0)
        {
            reason = "All extracted tracks were rejected by safety filters.";
            return false;
        }

        global::OpenTK.Mathematics.Matrix4[]? unmappedMeshRestLocals = null;
        if (hasUnmappedMeshBone)
        {
            var bindPoses = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
            for (var i = 0; i < meshBoneCount; i++)
            {
                try { bindPoses[i] = skeleton.BindPoseInverses[i].Inverted(); }
                catch { bindPoses[i] = global::OpenTK.Mathematics.Matrix4.Identity; }
            }

            unmappedMeshRestLocals = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
            for (var i = 0; i < meshBoneCount; i++)
            {
                var pIdx = skeleton.MeshParentIndices[i];
                unmappedMeshRestLocals[i] = pIdx >= 0 && pIdx < meshBoneCount
                    ? skeleton.BindPoseInverses[i] * bindPoses[pIdx]
                    : skeleton.BindPoseInverses[i];
            }
        }

        if (sampleRate <= 0f || !float.IsFinite(sampleRate))
        {
            sampleRate = 30f;
        }

        resolvedSampleRate = sampleRate;
        if (maxTime <= 0f || !float.IsFinite(maxTime))
        {
            maxTime = 1f;
        }

        var frameCount = useCeilingFrameCount
            ? Math.Max(1, (int)Math.Ceiling(maxTime * sampleRate))
            : Math.Max(1, (int)(maxTime * sampleRate));

        allFrames = new global::OpenTK.Mathematics.Vector3[frameCount][];
        allBoneMatrices = new global::OpenTK.Mathematics.Matrix4[frameCount][];

        var hasPosMask = new bool[skelCount];
        var hasRotMask = new bool[skelCount];
        var hasScaleMask = new bool[skelCount];
        foreach (var k in posTracks.Keys) if ((uint)k < (uint)skelCount) hasPosMask[k] = true;
        foreach (var k in rotTracks.Keys) if ((uint)k < (uint)skelCount) hasRotMask[k] = true;
        foreach (var k in scaleTracks.Keys) if ((uint)k < (uint)skelCount) hasScaleMask[k] = true;

        var skelWorldMatrices = new global::OpenTK.Mathematics.Matrix4?[skelCount];
        var visiting = new bool[skelCount];

        for (var f = 0; f < frameCount; f++)
        {
            var t = f / sampleRate;
            Array.Clear(skelWorldMatrices, 0, skelCount);
            Array.Clear(visiting, 0, skelCount);

            global::OpenTK.Mathematics.Matrix4 GetSkelModelMatrix(int sIdx)
            {
                if (skelWorldMatrices[sIdx] is global::OpenTK.Mathematics.Matrix4 cached)
                {
                    return cached;
                }

                if (visiting[sIdx])
                {
                    return skelRestLocals[sIdx];
                }

                visiting[sIdx] = true;
                var localMat = skelRestLocals[sIdx];
                var hasPos = hasPosMask[sIdx];
                var hasRot = hasRotMask[sIdx];
                var hasScale = hasScaleMask[sIdx];
                if (hasPos || hasRot || hasScale)
                {
                    var pos = hasPos ? EvalPos(sIdx, t) : skelRestPos[sIdx];
                    var rot = hasRot ? EvalRot(sIdx, t) : skelRestRot[sIdx];
                    var scale = hasScale ? EvalScale(sIdx, t) : skelRestScale[sIdx];
                    localMat = MeshPreviewSkeletonBuilder.CreateLocalMatrix(pos, rot, scale);
                }

                var p = sIdx < avatarParents.Length ? avatarParents[sIdx] : -1;
                var worldMat = p >= 0 && p < skelCount && p != sIdx
                    ? localMat * GetSkelModelMatrix(p)
                    : localMat;

                visiting[sIdx] = false;
                skelWorldMatrices[sIdx] = worldMat;
                return worldMat;
            }

            var framePositions = new global::OpenTK.Mathematics.Vector3[meshBoneCount];
            var frameMatrices = new global::OpenTK.Mathematics.Matrix4[meshBoneCount];
            var meshResolved = new bool[meshBoneCount];

            global::OpenTK.Mathematics.Matrix4 GetMeshBoneMatrix(int mb)
            {
                if (meshResolved[mb])
                {
                    return frameMatrices[mb];
                }

                var skelIdx = mb < skeleton.MeshBoneToSkelNode.Length ? skeleton.MeshBoneToSkelNode[mb] : -1;
                global::OpenTK.Mathematics.Matrix4 mat;
                if (skelIdx >= 0 && skelIdx < skelCount)
                {
                    mat = GetSkelModelMatrix(skelIdx);
                }
                else
                {
                    var localMat = unmappedMeshRestLocals != null ? unmappedMeshRestLocals[mb] : skeleton.BindPoseInverses[mb];
                    var pIdx = skeleton.MeshParentIndices[mb];
                    meshResolved[mb] = true;
                    mat = pIdx >= 0 && pIdx != mb && pIdx < meshBoneCount
                        ? localMat * GetMeshBoneMatrix(pIdx)
                        : localMat;
                }

                frameMatrices[mb] = mat;
                framePositions[mb] = mat.ExtractTranslation();
                meshResolved[mb] = true;
                return mat;
            }

            for (var mb = 0; mb < meshBoneCount; mb++)
            {
                GetMeshBoneMatrix(mb);
            }

            allFrames[f] = framePositions;
            allBoneMatrices[f] = frameMatrices;
        }

        var meshParentIndices = skeleton.MeshParentIndices;
        var meshBoneNames = skeleton.MeshBoneNames;
        renderParentIndices = meshParentIndices.Take(meshBoneCount).ToArray();
        var hiddenRenderBones = new bool[meshBoneCount];
        for (var i = 0; i < meshBoneCount; i++)
        {
            var boneName = i < meshBoneNames.Length ? meshBoneNames[i] : string.Empty;
            if (!deformChainMask[i] || (!weightedBoneMask[i] && MeshPreviewSkeletonBuilder.IsAuxiliaryAnimationBone(boneName)))
            {
                hiddenRenderBones[i] = true;
                renderParentIndices[i] = -1;
            }
        }

        var maxRestEdge = 0f;
        for (var i = 0; i < meshBoneCount; i++)
        {
            var parentIdx = meshParentIndices[i];
            if (parentIdx >= 0 && parentIdx < meshBoneCount
                && MeshPreviewSkeletonBuilder.IsFiniteVector(avatarRestBonePositions[i])
                && MeshPreviewSkeletonBuilder.IsFiniteVector(avatarRestBonePositions[parentIdx]))
            {
                maxRestEdge = Math.Max(maxRestEdge, (avatarRestBonePositions[i] - avatarRestBonePositions[parentIdx]).Length);
            }
        }

        var edgeLimit = Math.Max(Math.Max(maxRestEdge * 8f, restExtent * 1.5f), 0.5f);
        for (var i = 0; i < meshBoneCount; i++)
        {
            var parentIdx = meshParentIndices[i];
            if (parentIdx < 0 || parentIdx >= meshBoneCount)
            {
                continue;
            }

            foreach (var frame in allFrames)
            {
                if (!MeshPreviewSkeletonBuilder.IsFiniteVector(frame[i])
                    || !MeshPreviewSkeletonBuilder.IsFiniteVector(frame[parentIdx])
                    || (frame[i] - frame[parentIdx]).Length > edgeLimit)
                {
                    hiddenRenderBones[i] = true;
                    renderParentIndices[i] = -1;
                    break;
                }
            }
        }

        for (var f = 0; f < allFrames.Length; f++)
        {
            for (var i = 0; i < meshBoneCount; i++)
            {
                if (!hiddenRenderBones[i])
                {
                    continue;
                }

                var parentIdx = meshParentIndices[i];
                allFrames[f][i] = parentIdx >= 0 && parentIdx < meshBoneCount
                    ? allFrames[f][parentIdx]
                    : avatarRestBonePositions[i];
            }
        }

        return true;
    }

    public static int ExtractHumanoidMuscleTracksForAvatarSkeleton(
        AnimationClip clip,
        Avatar avatar,
        AvatarDefaultSkeletonData skeleton,
        Func<int, bool> shouldUseNode,
        AnimationTrackSet tracks)
    {
        if (clip == null || avatar?.m_Avatar?.m_AvatarSkeleton?.m_ID == null || skeleton == null)
        {
            return 0;
        }

        var decodedTracks = HumanoidMuscleDecoder.DecodeHumanoidClip(clip, avatar);
        if (decodedTracks.Count == 0)
        {
            return 0;
        }

        var skelIds = avatar.m_Avatar.m_AvatarSkeleton.m_ID;
        var addedTracks = 0;
        for (var i = 0; i < decodedTracks.Count; i++)
        {
            var decoded = decodedTracks[i];
            int targetIdx = -1;
            if (decoded.AvatarNodeIndex >= 0 && decoded.AvatarNodeIndex < skeleton.NodeCount)
            {
                targetIdx = decoded.AvatarNodeIndex;
            }
            else if (decoded.AvatarNodeIndex >= 0 && decoded.AvatarNodeIndex < skelIds.Length
                && skeleton.NodeHashToIdx.TryGetValue(skelIds[decoded.AvatarNodeIndex], out var byHash))
            {
                targetIdx = byHash;
            }
            else if (!string.IsNullOrEmpty(decoded.BonePath)
                && MeshPreviewSkeletonBuilder.TryResolvePathAlias(skeleton.NodePathToIdx, decoded.BonePath, out var byPath))
            {
                targetIdx = byPath;
            }

            if (targetIdx < 0 || !shouldUseNode(targetIdx))
            {
                continue;
            }

            var addedAny = false;
            if (!tracks.PosTracks.ContainsKey(targetIdx) && decoded.Translations.Count > 0)
            {
                for (var k = 0; k < decoded.Translations.Count; k++)
                {
                    var kf = decoded.Translations[k];
                    tracks.AddPosition(targetIdx, kf.time, MeshPreviewSkeletonBuilder.ToOtkVector3(kf.value));
                }
                addedAny = true;
            }

            if (!tracks.RotTracks.ContainsKey(targetIdx) && decoded.Rotations.Count > 0)
            {
                for (var k = 0; k < decoded.Rotations.Count; k++)
                {
                    var kf = decoded.Rotations[k];
                    tracks.AddRotation(targetIdx, kf.time, MeshPreviewSkeletonBuilder.ToOtkQuaternion(kf.value));
                }
                addedAny = true;
            }

            if (addedAny)
            {
                addedTracks++;
            }
        }

        return addedTracks;
    }

    /// <summary>
    /// Evaluates forward kinematics directly across the FULL Avatar skeleton hierarchy
    /// (without requiring any SkinnedMeshRenderer or m_BindPose), producing world positions
    /// and world matrices for every Avatar node per frame. Used for composite multi-part
    /// skinned+rigid characters as well as rigid-only chain/prop Avatars.
    /// </summary>
    public static bool EvaluateAvatarSkeletonFrames(
        AvatarDefaultSkeletonData skeleton,
        bool[]? activeNodeMask,
        bool[]? deformChainMask,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>> posTracks,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Quaternion value)>> rotTracks,
        Dictionary<int, List<(float time, global::OpenTK.Mathematics.Vector3 value)>> scaleTracks,
        float sampleRate,
        float maxTime,
        bool useCeilingFrameCount,
        bool failIfAllTracksFiltered,
        out global::OpenTK.Mathematics.Vector3[][] allNodeFrames,
        out global::OpenTK.Mathematics.Matrix4[][] allNodeWorldMatrices,
        out int[] renderParentIndices,
        out float resolvedSampleRate,
        out string reason)
    {
        allNodeFrames = Array.Empty<global::OpenTK.Mathematics.Vector3[]>();
        allNodeWorldMatrices = Array.Empty<global::OpenTK.Mathematics.Matrix4[]>();
        renderParentIndices = Array.Empty<int>();
        resolvedSampleRate = sampleRate;
        reason = string.Empty;

        var skelCount = skeleton?.NodeCount ?? 0;
        if (skeleton == null || skelCount == 0)
        {
            reason = "No Avatar skeleton nodes available for frame generation.";
            return false;
        }

        static int FindSegment<T>(List<(float time, T value)> track, float t)
        {
            int lo = 0;
            int hi = track.Count - 2;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (track[mid + 1].time < t)
                {
                    lo = mid + 1;
                }
                else if (track[mid].time > t)
                {
                    hi = mid - 1;
                }
                else
                {
                    return mid;
                }
            }

            return Math.Clamp(lo, 0, track.Count - 2);
        }

        global::OpenTK.Mathematics.Vector3 EvalPos(int nodeIdx, float t)
        {
            if (!posTracks.TryGetValue(nodeIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.Zero;
            if (track.Count == 1 || t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;
            var idx = FindSegment(track, t);
            var span = track[idx + 1].time - track[idx].time;
            var factor = span > 1e-6f ? (t - track[idx].time) / span : 0f;
            return global::OpenTK.Mathematics.Vector3.Lerp(track[idx].value, track[idx + 1].value, factor);
        }

        global::OpenTK.Mathematics.Quaternion EvalRot(int nodeIdx, float t)
        {
            if (!rotTracks.TryGetValue(nodeIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Quaternion.Identity;
            if (track.Count == 1 || t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;
            var idx = FindSegment(track, t);
            var span = track[idx + 1].time - track[idx].time;
            var factor = span > 1e-6f ? (t - track[idx].time) / span : 0f;
            return global::OpenTK.Mathematics.Quaternion.Slerp(track[idx].value, track[idx + 1].value, factor);
        }

        global::OpenTK.Mathematics.Vector3 EvalScale(int nodeIdx, float t)
        {
            if (!scaleTracks.TryGetValue(nodeIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.One;
            if (track.Count == 1 || t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;
            var idx = FindSegment(track, t);
            var span = track[idx + 1].time - track[idx].time;
            var factor = span > 1e-6f ? (t - track[idx].time) / span : 0f;
            return global::OpenTK.Mathematics.Vector3.Lerp(track[idx].value, track[idx + 1].value, factor);
        }

        var avatarParents = skeleton.ParentIndices;
        var skelRestPos = skeleton.RestLocalPositions;
        var skelRestRot = skeleton.RestLocalRotations;
        var skelRestScale = skeleton.RestLocalScales;
        var skelRestLocals = skeleton.RestLocalMatrices;
        var skelRestWorlds = skeleton.RestWorldMatrices;
        var skelRestWorldPositions = skeleton.RestWorldPositions;

        var restMin = new global::OpenTK.Mathematics.Vector3(float.MaxValue);
        var restMax = new global::OpenTK.Mathematics.Vector3(float.MinValue);
        for (var i = 0; i < skelCount; i++)
        {
            var pos = skelRestWorldPositions[i];
            if (MeshPreviewSkeletonBuilder.IsFiniteVector(pos))
            {
                restMin = global::OpenTK.Mathematics.Vector3.ComponentMin(restMin, pos);
                restMax = global::OpenTK.Mathematics.Vector3.ComponentMax(restMax, pos);
            }
        }

        var restExtent = float.IsFinite(restMin.X) && float.IsFinite(restMax.X)
            ? (restMax - restMin).Length
            : 1f;
        if (!float.IsFinite(restExtent) || restExtent <= 0f)
        {
            restExtent = 1f;
        }

        foreach (var item in posTracks.ToArray())
        {
            var nodeIdx = item.Key;
            if (nodeIdx < 0 || nodeIdx >= skelCount)
            {
                posTracks.Remove(nodeIdx);
                continue;
            }

            var p = nodeIdx < avatarParents.Length ? avatarParents[nodeIdx] : -1;
            if (p < 0 || p >= skelCount)
            {
                continue;
            }

            var parentWorld = skelRestWorlds[p];
            var sx = parentWorld.Row0.Xyz.Length;
            var sy = parentWorld.Row1.Xyz.Length;
            var sz = parentWorld.Row2.Xyz.Length;
            var minParentScale = Math.Min(sx, Math.Min(sy, sz));
            var safeParentScale = float.IsFinite(minParentScale) && minParentScale > 1e-4f ? minParentScale : 1f;
            var localPositionLimit = Math.Max(1f, (restExtent * 2f) / safeParentScale);

            var restLocalPosition = skelRestPos[nodeIdx];
            if (item.Value.Any(x => !MeshPreviewSkeletonBuilder.IsFiniteVector(x.value) || (x.value - restLocalPosition).Length > localPositionLimit))
            {
                posTracks.Remove(nodeIdx);
            }
        }

        foreach (var item in scaleTracks.ToArray())
        {
            var nodeIdx = item.Key;
            if (nodeIdx < 0 || nodeIdx >= skelCount)
            {
                scaleTracks.Remove(nodeIdx);
                continue;
            }

            var restScale = skelRestScale[nodeIdx];
            var restScaleMag = Math.Max(Math.Abs(restScale.X), Math.Max(Math.Abs(restScale.Y), Math.Abs(restScale.Z)));
            var maxScaleLimit = Math.Max(10f, float.IsFinite(restScaleMag) ? restScaleMag * 10f : 10f);
            if (item.Value.Any(x => !MeshPreviewSkeletonBuilder.IsFiniteVector(x.value)
                || x.value.X <= 0f || x.value.Y <= 0f || x.value.Z <= 0f
                || Math.Abs(x.value.X) > maxScaleLimit || Math.Abs(x.value.Y) > maxScaleLimit || Math.Abs(x.value.Z) > maxScaleLimit))
            {
                scaleTracks.Remove(nodeIdx);
            }
        }

        if (failIfAllTracksFiltered && posTracks.Count == 0 && rotTracks.Count == 0 && scaleTracks.Count == 0)
        {
            reason = "All extracted tracks were rejected by safety filters.";
            return false;
        }

        if (sampleRate <= 0f || !float.IsFinite(sampleRate))
        {
            sampleRate = 30f;
        }

        resolvedSampleRate = sampleRate;
        if (maxTime <= 0f || !float.IsFinite(maxTime))
        {
            maxTime = 1f;
        }

        var frameCount = useCeilingFrameCount
            ? Math.Max(1, (int)Math.Ceiling(maxTime * sampleRate))
            : Math.Max(1, (int)(maxTime * sampleRate));

        allNodeFrames = new global::OpenTK.Mathematics.Vector3[frameCount][];
        allNodeWorldMatrices = new global::OpenTK.Mathematics.Matrix4[frameCount][];

        var hasPosMask = new bool[skelCount];
        var hasRotMask = new bool[skelCount];
        var hasScaleMask = new bool[skelCount];
        foreach (var k in posTracks.Keys) if ((uint)k < (uint)skelCount) hasPosMask[k] = true;
        foreach (var k in rotTracks.Keys) if ((uint)k < (uint)skelCount) hasRotMask[k] = true;
        foreach (var k in scaleTracks.Keys) if ((uint)k < (uint)skelCount) hasScaleMask[k] = true;

        var skelWorldCache = new global::OpenTK.Mathematics.Matrix4?[skelCount];
        var visiting = new bool[skelCount];

        for (var f = 0; f < frameCount; f++)
        {
            var t = f / sampleRate;
            Array.Clear(skelWorldCache, 0, skelCount);
            Array.Clear(visiting, 0, skelCount);

            global::OpenTK.Mathematics.Matrix4 GetSkelModelMatrix(int sIdx)
            {
                if (skelWorldCache[sIdx] is global::OpenTK.Mathematics.Matrix4 cached)
                {
                    return cached;
                }

                if (visiting[sIdx])
                {
                    return skelRestLocals[sIdx];
                }

                visiting[sIdx] = true;
                var localMat = skelRestLocals[sIdx];
                var hasPos = hasPosMask[sIdx];
                var hasRot = hasRotMask[sIdx];
                var hasScale = hasScaleMask[sIdx];
                if (hasPos || hasRot || hasScale)
                {
                    var pos = hasPos ? EvalPos(sIdx, t) : skelRestPos[sIdx];
                    var rot = hasRot ? EvalRot(sIdx, t) : skelRestRot[sIdx];
                    var scale = hasScale ? EvalScale(sIdx, t) : skelRestScale[sIdx];
                    localMat = MeshPreviewSkeletonBuilder.CreateLocalMatrix(pos, rot, scale);
                }

                var p = sIdx < avatarParents.Length ? avatarParents[sIdx] : -1;
                var worldMat = p >= 0 && p < skelCount && p != sIdx
                    ? localMat * GetSkelModelMatrix(p)
                    : localMat;

                visiting[sIdx] = false;
                skelWorldCache[sIdx] = worldMat;
                return worldMat;
            }

            var framePositions = new global::OpenTK.Mathematics.Vector3[skelCount];
            var frameMatrices = new global::OpenTK.Mathematics.Matrix4[skelCount];
            for (var s = 0; s < skelCount; s++)
            {
                var mat = GetSkelModelMatrix(s);
                frameMatrices[s] = mat;
                framePositions[s] = mat.ExtractTranslation();
            }

            allNodeFrames[f] = framePositions;
            allNodeWorldMatrices[f] = frameMatrices;
        }

        renderParentIndices = avatarParents.Take(skelCount).ToArray();
        var hiddenRenderBones = new bool[skelCount];
        var hasDeformMask = deformChainMask != null && deformChainMask.Length == skelCount;
        var hasActiveMask = activeNodeMask != null && activeNodeMask.Length == skelCount;
        for (var i = 0; i < skelCount; i++)
        {
            var boneName = i < skeleton.NodeNames.Length ? skeleton.NodeNames[i] : string.Empty;
            var inDeformChain = !hasDeformMask || deformChainMask![i];
            var isActive = hasActiveMask && activeNodeMask![i];
            if (!inDeformChain || (!isActive && MeshPreviewSkeletonBuilder.IsAuxiliaryAnimationBone(boneName)))
            {
                hiddenRenderBones[i] = true;
                renderParentIndices[i] = -1;
            }
        }

        var maxRestEdge = 0f;
        for (var i = 0; i < skelCount; i++)
        {
            var parentIdx = avatarParents[i];
            if (parentIdx >= 0 && parentIdx < skelCount
                && MeshPreviewSkeletonBuilder.IsFiniteVector(skelRestWorldPositions[i])
                && MeshPreviewSkeletonBuilder.IsFiniteVector(skelRestWorldPositions[parentIdx]))
            {
                maxRestEdge = Math.Max(maxRestEdge, (skelRestWorldPositions[i] - skelRestWorldPositions[parentIdx]).Length);
            }
        }

        var edgeLimit = Math.Max(Math.Max(maxRestEdge * 8f, restExtent * 1.5f), 0.5f);
        for (var i = 0; i < skelCount; i++)
        {
            var parentIdx = avatarParents[i];
            if (parentIdx < 0 || parentIdx >= skelCount)
            {
                continue;
            }

            foreach (var frame in allNodeFrames)
            {
                if (!MeshPreviewSkeletonBuilder.IsFiniteVector(frame[i])
                    || !MeshPreviewSkeletonBuilder.IsFiniteVector(frame[parentIdx])
                    || (frame[i] - frame[parentIdx]).Length > edgeLimit)
                {
                    hiddenRenderBones[i] = true;
                    renderParentIndices[i] = -1;
                    break;
                }
            }
        }

        for (var f = 0; f < allNodeFrames.Length; f++)
        {
            for (var i = 0; i < skelCount; i++)
            {
                if (!hiddenRenderBones[i])
                {
                    continue;
                }

                var parentIdx = avatarParents[i];
                allNodeFrames[f][i] = parentIdx >= 0 && parentIdx < skelCount
                    ? allNodeFrames[f][parentIdx]
                    : skelRestWorldPositions[i];
            }
        }

        return true;
    }
}
