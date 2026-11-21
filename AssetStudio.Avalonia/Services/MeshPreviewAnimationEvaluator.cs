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

        if (innerClip.m_StreamedClip != null)
        {
            var streamedFrames = innerClip.m_StreamedClip.ReadData();
            var frameValues = new float[totalCurveSlots];
            var frameValueStamp = new int[totalCurveSlots];
            var touchedBindings = new int[bindingCount];
            var touchedBindingStamp = new int[bindingCount];
            var currentStamp = 0;

            for (var frameIndex = 1; frameIndex < streamedFrames.Count - 1; frameIndex++)
            {
                var frame = streamedFrames[frameIndex];
                var keyList = frame.keyList;
                if (keyList == null || keyList.Length == 0)
                {
                    continue;
                }

                currentStamp++;
                var touchedCount = 0;
                for (var k = 0; k < keyList.Length; k++)
                {
                    var key = keyList[k];
                    if (!TryFindBindingIndex(key.index, out var bIdx))
                    {
                        continue;
                    }

                    // Keep first occurrence per index to match ToDictionary behavior without throwing or allocating
                    if (frameValueStamp[key.index] != currentStamp)
                    {
                        frameValueStamp[key.index] = currentStamp;
                        frameValues[key.index] = key.value;
                    }

                    if (bindingIsValidTransform[bIdx] && touchedBindingStamp[bIdx] != currentStamp)
                    {
                        touchedBindingStamp[bIdx] = currentStamp;
                        touchedBindings[touchedCount++] = bIdx;
                    }
                }

                if (touchedCount > 1)
                {
                    Array.Sort(touchedBindings, 0, touchedCount);
                }

                for (var t = 0; t < touchedCount; t++)
                {
                    var bIdx = touchedBindings[t];
                    var start = bindingStartIndices[bIdx];
                    var width = bindingWidths[bIdx];
                    var hasAllComponents = true;
                    for (var c = 0; c < width; c++)
                    {
                        if (frameValueStamp[start + c] != currentStamp)
                        {
                            hasAllComponents = false;
                            break;
                        }
                    }

                    if (hasAllComponents)
                    {
                        tracks.AddGenericKeyframe(
                            bindingMeshBones[bIdx],
                            bindingAttributes[bIdx],
                            frame.time,
                            frameValues,
                            start,
                            updateMaxTimeBeforeBoundsCheck);
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
        var skelNodeToMeshBone = new Dictionary<int, int>(skeleton.MeshBoneToSkelNode.Length);
        for (var mb = 0; mb < skeleton.MeshBoneToSkelNode.Length; mb++)
        {
            var sn = skeleton.MeshBoneToSkelNode[mb];
            if (sn >= 0)
            {
                skelNodeToMeshBone[sn] = mb;
            }
        }

        var addedTracks = 0;
        for (var i = 0; i < decodedTracks.Count; i++)
        {
            var decoded = decodedTracks[i];
            int meshBoneIdx = -1;
            if (!skelNodeToMeshBone.TryGetValue(decoded.AvatarNodeIndex, out meshBoneIdx))
            {
                if (decoded.AvatarNodeIndex >= 0 && decoded.AvatarNodeIndex < skelIds.Length
                    && skeleton.MeshBoneHashToIdx.TryGetValue(skelIds[decoded.AvatarNodeIndex], out var byHash))
                {
                    meshBoneIdx = byHash;
                }
                else if (!string.IsNullOrEmpty(decoded.BonePath)
                    && MeshPreviewSkeletonBuilder.TryResolvePathAlias(meshBonePathToIdx, decoded.BonePath, out var byPath))
                {
                    meshBoneIdx = byPath;
                }
            }

            if (meshBoneIdx < 0 || !shouldUseBone(meshBoneIdx))
            {
                continue;
            }

            bool addedAny = false;
            if (!tracks.PosTracks.ContainsKey(meshBoneIdx) && decoded.Translations.Count > 0)
            {
                for (var k = 0; k < decoded.Translations.Count; k++)
                {
                    var kf = decoded.Translations[k];
                    tracks.AddPosition(meshBoneIdx, kf.time, MeshPreviewSkeletonBuilder.ToOtkVector3(kf.value));
                }
                addedAny = true;
            }

            if (!tracks.RotTracks.ContainsKey(meshBoneIdx) && decoded.Rotations.Count > 0)
            {
                for (var k = 0; k < decoded.Rotations.Count; k++)
                {
                    var kf = decoded.Rotations[k];
                    tracks.AddRotation(meshBoneIdx, kf.time, MeshPreviewSkeletonBuilder.ToOtkQuaternion(kf.value));
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
                    ? bindPoses[i] * bindPoseInverses[pIdx]
                    : bindPoses[i];
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
}
