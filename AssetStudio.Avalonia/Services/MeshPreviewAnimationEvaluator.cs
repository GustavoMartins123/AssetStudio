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
                var euler = new global::OpenTK.Mathematics.Vector3(
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset]),
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset + 1]),
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(data[offset + 2]));
                AddRotation(meshBoneIdx, time, global::OpenTK.Mathematics.Quaternion.FromEulerAngles(euler));
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

            foreach (var key in curve.curve.m_Curve)
            {
                var euler = MeshPreviewSkeletonBuilder.ToOtkVector3(key.value);
                euler = new global::OpenTK.Mathematics.Vector3(
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(euler.X),
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(euler.Y),
                    global::OpenTK.Mathematics.MathHelper.DegreesToRadians(euler.Z));
                tracks.AddRotation(boneIdx, key.time, global::OpenTK.Mathematics.Quaternion.FromEulerAngles(euler));
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
        if (bindings.genericBindings == null)
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

        bool TryFindBindingInfo(int index, out GenericBinding binding, out int startIndex, out int width)
        {
            var curves = 0;
            foreach (var candidate in bindings.genericBindings)
            {
                var candidateWidth = GetBindingCurveWidth(candidate);
                if (index >= curves && index < curves + candidateWidth)
                {
                    binding = candidate;
                    startIndex = curves;
                    width = candidateWidth;
                    return true;
                }

                curves += candidateWidth;
            }

            binding = default!;
            startIndex = -1;
            width = 1;
            return false;
        }

        void ProcessCurveData(int curveIndexInStream, float time, float[] data, int dataOffset, ref int currentIdxOut)
        {
            if (!TryFindBindingInfo(curveIndexInStream, out var binding, out var bindingStart, out var bindingWidth)
                || curveIndexInStream != bindingStart)
            {
                currentIdxOut++;
                return;
            }

            if (binding.typeID == ClassIDType.Transform)
            {
                if (tryResolveBone(binding, out var meshBoneIdx) && shouldUseBone(meshBoneIdx))
                {
                    if (binding.attribute is 1 or 2 or 3 or 4)
                    {
                        tracks.AddGenericKeyframe(
                            meshBoneIdx,
                            binding.attribute,
                            time,
                            data,
                            currentIdxOut + dataOffset,
                            updateMaxTimeBeforeBoundsCheck);
                        currentIdxOut += bindingWidth;
                    }
                    else
                    {
                        currentIdxOut++;
                    }
                }
                else
                {
                    currentIdxOut += bindingWidth;
                }
            }
            else
            {
                currentIdxOut++;
            }
        }

        void ProcessStreamedFrame(float time, StreamedClip.StreamedCurveKey[] keyList)
        {
            var valuesByIndex = keyList.ToDictionary(x => x.index, x => x.value);
            var processedStarts = new HashSet<int>();

            foreach (var key in keyList.OrderBy(x => x.index))
            {
                if (!TryFindBindingInfo(key.index, out var binding, out var bindingStart, out var bindingWidth)
                    || !processedStarts.Add(bindingStart)
                    || binding.typeID != ClassIDType.Transform
                    || !tryResolveBone(binding, out var meshBoneIdx)
                    || !shouldUseBone(meshBoneIdx))
                {
                    continue;
                }

                var values = new float[bindingWidth];
                var hasAllComponents = true;
                for (var i = 0; i < bindingWidth; i++)
                {
                    if (valuesByIndex.TryGetValue(bindingStart + i, out var value))
                    {
                        values[i] = value;
                    }
                    else
                    {
                        hasAllComponents = false;
                        break;
                    }
                }

                if (hasAllComponents)
                {
                    tracks.AddGenericKeyframe(
                        meshBoneIdx,
                        binding.attribute,
                        time,
                        values,
                        0,
                        updateMaxTimeBeforeBoundsCheck);
                }
            }
        }

        if (innerClip.m_StreamedClip != null)
        {
            var streamedFrames = innerClip.m_StreamedClip.ReadData();
            for (var frameIndex = 1; frameIndex < streamedFrames.Count - 1; frameIndex++)
            {
                var frame = streamedFrames[frameIndex];
                ProcessStreamedFrame(frame.time, frame.keyList);
            }
        }

        if (innerClip.m_DenseClip != null)
        {
            var dense = innerClip.m_DenseClip;
            var streamCount = innerClip.m_StreamedClip?.curveCount ?? 0;
            for (var frameIndex = 0; frameIndex < dense.m_FrameCount; frameIndex++)
            {
                var time = dense.m_BeginTime + frameIndex / dense.m_SampleRate;
                var frameOffset = frameIndex * dense.m_CurveCount;
                for (var cIdx = 0; cIdx < dense.m_CurveCount;)
                {
                    ProcessCurveData((int)(streamCount + cIdx), time, dense.m_SampleArray, (int)frameOffset, ref cIdx);
                }
            }
        }

        if (innerClip.m_ConstantClip != null)
        {
            var constant = innerClip.m_ConstantClip;
            var denseCount = innerClip.m_DenseClip?.m_CurveCount ?? 0;
            var streamCount = innerClip.m_StreamedClip?.curveCount ?? 0;
            var time = 0.0f;
            for (var i = 0; i < 2; i++)
            {
                for (var cIdx = 0; cIdx < constant.data.Length;)
                {
                    ProcessCurveData((int)(streamCount + denseCount + cIdx), time, constant.data, 0, ref cIdx);
                }

                time = constantStopTime ?? time;
            }
        }
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

        global::OpenTK.Mathematics.Vector3 EvaluatePos(int meshBoneIdx, float t)
        {
            if (!posTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.Zero;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (var i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    var factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Vector3.Lerp(track[i].value, track[i + 1].value, factor);
                }
            }

            return track[^1].value;
        }

        global::OpenTK.Mathematics.Quaternion EvaluateRot(int meshBoneIdx, float t)
        {
            if (!rotTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Quaternion.Identity;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (var i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    var factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Quaternion.Slerp(track[i].value, track[i + 1].value, factor);
                }
            }

            return track[^1].value;
        }

        global::OpenTK.Mathematics.Vector3 EvaluateScale(int meshBoneIdx, float t)
        {
            if (!scaleTracks.TryGetValue(meshBoneIdx, out var track) || track.Count == 0) return global::OpenTK.Mathematics.Vector3.One;
            if (track.Count == 1) return track[0].value;
            if (t <= track[0].time) return track[0].value;
            if (t >= track[^1].time) return track[^1].value;

            for (var i = 0; i < track.Count - 1; i++)
            {
                if (t >= track[i].time && t <= track[i + 1].time)
                {
                    var factor = (t - track[i].time) / (track[i + 1].time - track[i].time);
                    return global::OpenTK.Mathematics.Vector3.Lerp(track[i].value, track[i + 1].value, factor);
                }
            }

            return track[^1].value;
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
