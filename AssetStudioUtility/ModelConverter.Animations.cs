using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AssetStudio
{
    public partial class ModelConverter
    {
        private void ConvertAnimations()
        {
            foreach (var animationClip in animationClipHashSet)
            {
                var iAnim = new ImportedKeyframedAnimation();
                var name = animationClip.m_Name;
                if (AnimationList.Exists(x => x.Name == name))
                {
                    for (int i = 1; ; i++)
                    {
                        var fixName = name + $"_{i}";
                        if (!AnimationList.Exists(x => x.Name == fixName))
                        {
                            name = fixName;
                            break;
                        }
                    }
                }
                iAnim.Name = name;
                iAnim.SampleRate = animationClip.m_SampleRate;
                iAnim.TrackList = new List<ImportedAnimationKeyframedTrack>();
                AnimationList.Add(iAnim);
                if (animationClip.m_Legacy)
                {
                    foreach (var m_CompressedRotationCurve in animationClip.m_CompressedRotationCurves ?? Array.Empty<CompressedAnimationCurve>())
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_CompressedRotationCurve.m_Path));

                        var numKeys = m_CompressedRotationCurve.m_Times.m_NumItems;
                        var data = m_CompressedRotationCurve.m_Times.UnpackInts();
                        var times = new float[numKeys];
                        int t = 0;
                        for (int i = 0; i < numKeys; i++)
                        {
                            t += data[i];
                            times[i] = t * 0.01f;
                        }
                        var quats = m_CompressedRotationCurve.m_Values.UnpackQuats();

                        for (int i = 0; i < numKeys; i++)
                        {
                            var quat = quats[i];
                            var value = QuaternionToEuler(new Quaternion(quat.X, -quat.Y, -quat.Z, quat.W));
                            track.Rotations.Add(new ImportedKeyframe<Vector3>(times[i], value));
                        }
                    }
                    foreach (var m_RotationCurve in animationClip.m_RotationCurves ?? Array.Empty<QuaternionCurve>())
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_RotationCurve.path));
                        foreach (var m_Curve in m_RotationCurve.curve.m_Curve)
                        {
                            bool isStepped = float.IsInfinity(m_Curve.outSlope.X) || float.IsInfinity(m_Curve.outSlope.Y) || float.IsInfinity(m_Curve.outSlope.Z) || float.IsInfinity(m_Curve.outSlope.W);
                            var value = QuaternionToEuler(new Quaternion(m_Curve.value.X, -m_Curve.value.Y, -m_Curve.value.Z, m_Curve.value.W));
                            track.Rotations.Add(new ImportedKeyframe<Vector3>(m_Curve.time, value, isStepped));
                        }
                    }
                    foreach (var m_PositionCurve in animationClip.m_PositionCurves ?? Array.Empty<Vector3Curve>())
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_PositionCurve.path));
                        foreach (var m_Curve in m_PositionCurve.curve.m_Curve)
                        {
                            bool isStepped = float.IsInfinity(m_Curve.outSlope.X) || float.IsInfinity(m_Curve.outSlope.Y) || float.IsInfinity(m_Curve.outSlope.Z);
                            track.Translations.Add(new ImportedKeyframe<Vector3>(m_Curve.time, new Vector3(-m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z), isStepped));
                        }
                    }
                    foreach (var m_ScaleCurve in animationClip.m_ScaleCurves ?? Array.Empty<Vector3Curve>())
                    {
                        var track = iAnim.FindTrack(FixBonePath(animationClip, m_ScaleCurve.path));
                        foreach (var m_Curve in m_ScaleCurve.curve.m_Curve)
                        {
                            bool isStepped = float.IsInfinity(m_Curve.outSlope.X) || float.IsInfinity(m_Curve.outSlope.Y) || float.IsInfinity(m_Curve.outSlope.Z);
                            track.Scalings.Add(new ImportedKeyframe<Vector3>(m_Curve.time, new Vector3(m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z), isStepped));
                        }
                    }
                    if (animationClip.m_EulerCurves != null)
                    {
                        foreach (var m_EulerCurve in animationClip.m_EulerCurves)
                        {
                            var track = iAnim.FindTrack(FixBonePath(animationClip, m_EulerCurve.path));
                            int rotOrder = m_EulerCurve.curve.m_RotationOrder;
                            foreach (var m_Curve in m_EulerCurve.curve.m_Curve)
                            {
                                bool isStepped = float.IsInfinity(m_Curve.outSlope.X) || float.IsInfinity(m_Curve.outSlope.Y) || float.IsInfinity(m_Curve.outSlope.Z);
                                var qU = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(m_Curve.value.X, m_Curve.value.Y, m_Curve.value.Z, rotOrder);
                                var qF = new Quaternion(qU.X, -qU.Y, -qU.Z, qU.W);
                                track.Rotations.Add(new ImportedKeyframe<Vector3>(m_Curve.time, QuaternionToEuler(qF), isStepped));
                            }
                        }
                    }
                    foreach (var m_FloatCurve in animationClip.m_FloatCurves ?? Array.Empty<FloatCurve>())
                    {
                        if (m_FloatCurve.classID == ClassIDType.SkinnedMeshRenderer) //BlendShape
                        {
                            var channelName = NormalizeBlendShapeChannelName(m_FloatCurve.attribute);
                            var path = ResolveBlendShapeTrackPath(animationClip, m_FloatCurve.path, channelName);
                            var track = iAnim.FindTrack(path);
                            var blendShape = track.FindOrCreateBlendShape(channelName);
                            foreach (var m_Curve in m_FloatCurve.curve.m_Curve)
                            {
                                blendShape.Keyframes.Add(new ImportedKeyframe<float>(m_Curve.time, m_Curve.value, float.IsInfinity(m_Curve.outSlope)));
                            }
                        }
                        else if (m_FloatCurve.classID == ClassIDType.GameObject
                            && string.Equals(m_FloatCurve.attribute, "m_IsActive", StringComparison.Ordinal))
                        {
                            var path = FixBonePath(animationClip, m_FloatCurve.path);
                            var track = iAnim.FindTrack(path);
                            foreach (var m_Curve in m_FloatCurve.curve.m_Curve)
                            {
                                track.Visibilities.Add(new ImportedKeyframe<float>(m_Curve.time, m_Curve.value >= 0.5f ? 1.0f : 0.0f, isStepped: true));
                            }
                        }
                    }
                }
                else if (animationClip.m_MuscleClip?.m_Clip != null)
                {
                    var m_Clip = animationClip.m_MuscleClip.m_Clip;
                    var m_ClipBindingConstant = animationClip.m_ClipBindingConstant ?? m_Clip.ConvertValueArrayToGenericBinding();
                    float clipSampleRate = animationClip.m_SampleRate > 0f && float.IsFinite(animationClip.m_SampleRate)
                        ? animationClip.m_SampleRate
                        : 30f;

                    // 1. StreamedClip: evaluate per-binding via cubic Hermite evaluator (handles sparse partial frames,
                    //    skips sentinels 0 and Count-1, preserves stepped segments, and subdivides curved Hermite spans).
                    uint streamCount = m_Clip.m_StreamedClip?.curveCount ?? 0;
                    if (m_Clip.m_StreamedClip != null && streamCount > 0 && m_Clip.m_StreamedClip.data != null && m_Clip.m_StreamedClip.data.Length > 0)
                    {
                        List<StreamedClip.StreamedFrame>? streamedFrames = null;
                        try
                        {
                            streamedFrames = m_Clip.m_StreamedClip.ReadData();
                        }
                        catch
                        {
                            streamedFrames = null;
                        }
                        if (streamedFrames != null && streamedFrames.Count >= 3)
                        {
                            var streamedEval = new HumanoidMuscleDecoder.StreamedCurveEvaluator(streamedFrames);
                            ReadStreamedClipCurves(iAnim, animationClip, m_ClipBindingConstant, streamedEval, (int)streamCount, clipSampleRate);
                        }
                    }

                    // 2. DenseClip (with sample-rate > 0 guard)
                    var m_DenseClip = m_Clip.m_DenseClip;
                    if (m_DenseClip != null && m_DenseClip.m_FrameCount > 0 && m_DenseClip.m_CurveCount > 0 && m_DenseClip.m_SampleArray != null)
                    {
                        float denseSampleRate = m_DenseClip.m_SampleRate > 0f && float.IsFinite(m_DenseClip.m_SampleRate)
                            ? m_DenseClip.m_SampleRate
                            : clipSampleRate;
                        float beginTime = float.IsFinite(m_DenseClip.m_BeginTime) ? m_DenseClip.m_BeginTime : 0f;
                        for (int frameIndex = 0; frameIndex < m_DenseClip.m_FrameCount; frameIndex++)
                        {
                            var time = beginTime + frameIndex / denseSampleRate;
                            if (time < 0f) time = 0f;
                            var frameOffset = frameIndex * (int)m_DenseClip.m_CurveCount;
                            for (int curveIndex = 0; curveIndex < m_DenseClip.m_CurveCount;)
                            {
                                var index = streamCount + curveIndex;
                                ReadCurveData(iAnim, animationClip, m_ClipBindingConstant, (int)index, time, m_DenseClip.m_SampleArray, frameOffset, ref curveIndex);
                            }
                        }
                    }

                    // 3. ConstantClip (honor m_StartTime, avoid duplicate t=0 key when m_StopTime <= m_StartTime)
                    if (m_Clip.m_ConstantClip?.data != null && m_Clip.m_ConstantClip.data.Length > 0)
                    {
                        var m_ConstantClip = m_Clip.m_ConstantClip;
                        var denseCount = m_Clip.m_DenseClip?.m_CurveCount ?? 0;
                        float startTime = float.IsFinite(animationClip.m_MuscleClip.m_StartTime) && animationClip.m_MuscleClip.m_StartTime > 0f
                            ? animationClip.m_MuscleClip.m_StartTime
                            : 0f;
                        float stopTime = float.IsFinite(animationClip.m_MuscleClip.m_StopTime) && animationClip.m_MuscleClip.m_StopTime > startTime
                            ? animationClip.m_MuscleClip.m_StopTime
                            : startTime;
                        int passCount = stopTime > startTime + 1e-6f ? 2 : 1;
                        for (int i = 0; i < passCount; i++)
                        {
                            float time2 = i == 0 ? startTime : stopTime;
                            for (int curveIndex = 0; curveIndex < m_ConstantClip.data.Length;)
                            {
                                var index = streamCount + denseCount + curveIndex;
                                ReadCurveData(iAnim, animationClip, m_ClipBindingConstant, (int)index, time2, m_ConstantClip.data, 0, ref curveIndex);
                            }
                        }
                    }

                    // 4. Humanoid MuscleClip decoding
                    if (avatar != null)
                    {
                        ReadHumanoidMuscleCurves(iAnim, animationClip);
                    }
                }

                FinalizeAnimationTracks(iAnim);
            }
        }

        private void ReadStreamedClipCurves(
            ImportedKeyframedAnimation iAnim,
            AnimationClip animationClip,
            AnimationClipBindingConstant? m_ClipBindingConstant,
            HumanoidMuscleDecoder.StreamedCurveEvaluator evaluator,
            int streamCount,
            float clipSampleRate)
        {
            if (m_ClipBindingConstant?.genericBindings == null || !evaluator.HasAnyKeys || streamCount <= 0)
            {
                return;
            }

            float dtStep = 1f / Math.Clamp(clipSampleRate, 1f, 120f);
            var tempValues = new float[4];
            var bindingTimes = new List<float>();

            int slotCursor = 0;
            var bindings = m_ClipBindingConstant.genericBindings;
            for (int bIdx = 0; bIdx < bindings.Length; bIdx++)
            {
                var binding = bindings[bIdx];
                int width = GetGenericBindingCurveWidth(binding);
                int startSlot = slotCursor;
                slotCursor += width;

                if (startSlot >= streamCount)
                {
                    break;
                }

                bool isSupportedTransform = binding.typeID == ClassIDType.Transform && binding.attribute is 1 or 2 or 3 or 4;
                bool isBlendShape = binding.typeID == ClassIDType.SkinnedMeshRenderer;
                bool isVisibility = binding.typeID == ClassIDType.GameObject && binding.attribute == 2086281974;
                if (!isSupportedTransform && !isBlendShape && !isVisibility)
                {
                    continue;
                }

                // Collect union of key times across all component curves of this binding
                bindingTimes.Clear();
                int activeComponents = 0;
                for (int c = 0; c < width; c++)
                {
                    if (startSlot + c >= streamCount)
                    {
                        break;
                    }
                    if (evaluator.TryGetCurveKeys(startSlot + c, out var keys) && keys.Count > 0)
                    {
                        activeComponents++;
                        for (int k = 0; k < keys.Count; k++)
                        {
                            bindingTimes.Add(keys[k].Time);
                        }
                    }
                }

                if (activeComponents == 0 || bindingTimes.Count == 0)
                {
                    continue;
                }

                bindingTimes.Sort();
                int uniqueCount = 0;
                for (int i = 0; i < bindingTimes.Count; i++)
                {
                    if (uniqueCount == 0 || bindingTimes[i] - bindingTimes[uniqueCount - 1] > 1e-6f)
                    {
                        bindingTimes[uniqueCount++] = bindingTimes[i];
                    }
                }

                // Resolve target track & blendshape channel once per binding
                ImportedAnimationKeyframedTrack? track = null;
                ImportedBlendShape? blendShape = null;
                if (isBlendShape)
                {
                    var channelName = GetChannelNameFromHash(binding.attribute);
                    if (string.IsNullOrEmpty(channelName))
                    {
                        continue;
                    }
                    channelName = NormalizeBlendShapeChannelName(channelName);
                    var bPath = ResolveBlendShapeTrackPath(animationClip, GetPathFromHash(binding.path), channelName);
                    track = iAnim.FindTrack(bPath);
                    blendShape = track.FindOrCreateBlendShape(channelName);
                }
                else
                {
                    var path = FixBonePath(animationClip, GetPathFromHash(binding.path));
                    track = iAnim.FindTrack(path);
                }

                float curvatureTol = binding.typeID == ClassIDType.Transform && binding.attribute == 4 ? 0.05f : 1e-3f;

                void EmitSample(float sampleTime, bool stepped)
                {
                    for (int c = 0; c < width; c++)
                    {
                        float fallback = (binding.typeID == ClassIDType.Transform && binding.attribute == 3)
                            ? 1f
                            : (binding.typeID == ClassIDType.Transform && binding.attribute == 2 && c == 3 ? 1f : 0f);
                        tempValues[c] = evaluator.EvaluateOrDefault(startSlot + c, sampleTime, fallback);
                    }

                    if (isBlendShape && blendShape != null)
                    {
                        blendShape.Keyframes.Add(new ImportedKeyframe<float>(sampleTime, tempValues[0], stepped));
                    }
                    else if (isVisibility)
                    {
                        track.Visibilities.Add(new ImportedKeyframe<float>(sampleTime, tempValues[0] >= 0.5f ? 1.0f : 0.0f, isStepped: true));
                    }
                    else if (isSupportedTransform)
                    {
                        switch (binding.attribute)
                        {
                            case 1:
                                track.Translations.Add(new ImportedKeyframe<Vector3>(
                                    sampleTime,
                                    new Vector3(-tempValues[0], tempValues[1], tempValues[2]),
                                    stepped));
                                break;
                            case 2:
                                var eulerFromQuat = QuaternionToEuler(new Quaternion(tempValues[0], -tempValues[1], -tempValues[2], tempValues[3]));
                                track.Rotations.Add(new ImportedKeyframe<Vector3>(sampleTime, eulerFromQuat, stepped));
                                break;
                            case 3:
                                track.Scalings.Add(new ImportedKeyframe<Vector3>(
                                    sampleTime,
                                    new Vector3(tempValues[0], tempValues[1], tempValues[2]),
                                    stepped));
                                break;
                            case 4:
                                var qU = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(tempValues[0], tempValues[1], tempValues[2], 4);
                                var qF = new Quaternion(qU.X, -qU.Y, -qU.Z, qU.W);
                                track.Rotations.Add(new ImportedKeyframe<Vector3>(sampleTime, QuaternionToEuler(qF), stepped));
                                break;
                        }
                    }
                }

                for (int i = 0; i < uniqueCount; i++)
                {
                    float t0 = bindingTimes[i];
                    bool segStepped = isVisibility;
                    if (!segStepped)
                    {
                        segStepped = true;
                        for (int c = 0; c < width; c++)
                        {
                            if (evaluator.TryEvaluate(startSlot + c, t0, out _, out bool compStepped) && !compStepped)
                            {
                                segStepped = false;
                                break;
                            }
                        }
                    }

                    EmitSample(t0, segStepped);

                    // Adaptively subdivide non-stepped Hermite segments whose cubic midpoint diverges from linear
                    if (!segStepped && i + 1 < uniqueCount)
                    {
                        float t1 = bindingTimes[i + 1];
                        float span = t1 - t0;
                        if (span > dtStep * 1.25f)
                        {
                            float tMid = 0.5f * (t0 + t1);
                            bool needsSubdivision = false;
                            for (int c = 0; c < width; c++)
                            {
                                float v0 = evaluator.EvaluateOrDefault(startSlot + c, t0, 0f);
                                float v1 = evaluator.EvaluateOrDefault(startSlot + c, t1, 0f);
                                float vMid = evaluator.EvaluateOrDefault(startSlot + c, tMid, 0f);
                                if (MathF.Abs(vMid - 0.5f * (v0 + v1)) > curvatureTol)
                                {
                                    needsSubdivision = true;
                                    break;
                                }
                            }

                            if (needsSubdivision)
                            {
                                int subSteps = Math.Clamp((int)MathF.Ceiling(span / dtStep), 2, 120);
                                for (int s = 1; s < subSteps; s++)
                                {
                                    float ts = t0 + span * s / subSteps;
                                    EmitSample(ts, stepped: false);
                                }
                            }
                        }
                    }
                }
            }
        }

        private static int GetGenericBindingCurveWidth(GenericBinding binding)
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

        private string? ResolveBlendShapeTrackPath(AnimationClip animationClip, string? rawPath, string channelName)
        {
            var resolved = FixBonePath(animationClip, rawPath);
            if (!string.IsNullOrEmpty(resolved) && HasMorphWithPath(resolved))
            {
                return resolved;
            }

            var byChannel = GetPathByChannelName(channelName);
            if (!string.IsNullOrEmpty(byChannel))
            {
                return byChannel;
            }

            return resolved;
        }

        private bool HasMorphWithPath(string path)
        {
            var norm = NormalizeFramePath(path);
            for (int i = 0; i < MorphList.Count; i++)
            {
                if (string.Equals(NormalizeFramePath(MorphList[i].Path), norm, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private void ReadCurveData(ImportedKeyframedAnimation iAnim, AnimationClip animationClip, AnimationClipBindingConstant m_ClipBindingConstant, int index, float time, float[] data, int offset, ref int curveIndex)
        {
            var binding = m_ClipBindingConstant.FindBinding(index);
            if (binding == null)
            {
                curveIndex++;
                return;
            }
            if (binding.typeID == ClassIDType.SkinnedMeshRenderer) //BlendShape
            {
                var channelName = GetChannelNameFromHash(binding.attribute);
                if (string.IsNullOrEmpty(channelName))
                {
                    curveIndex++;
                    return;
                }
                channelName = NormalizeBlendShapeChannelName(channelName);

                var bPath = ResolveBlendShapeTrackPath(animationClip, GetPathFromHash(binding.path), channelName);
                var bTrack = iAnim.FindTrack(bPath);
                var blendShape = bTrack.FindOrCreateBlendShape(channelName);
                int idxVal = curveIndex++ + offset;
                if ((uint)idxVal < (uint)data.Length)
                {
                    blendShape.Keyframes.Add(new ImportedKeyframe<float>(time, data[idxVal]));
                }
            }
            else if (binding.typeID == ClassIDType.Transform)
            {
                var path = FixBonePath(animationClip, GetPathFromHash(binding.path));
                var track = iAnim.FindTrack(path);

                switch (binding.attribute)
                {
                    case 1:
                        if ((uint)(curveIndex + 2 + offset) < (uint)data.Length)
                        {
                            track.Translations.Add(new ImportedKeyframe<Vector3>(time, new Vector3
                            (
                                -data[curveIndex + offset],
                                data[curveIndex + 1 + offset],
                                data[curveIndex + 2 + offset]
                            )));
                        }
                        curveIndex += 3;
                        break;
                    case 2:
                        if ((uint)(curveIndex + 3 + offset) < (uint)data.Length)
                        {
                            var value = QuaternionToEuler(new Quaternion
                            (
                                data[curveIndex + offset],
                                -data[curveIndex + 1 + offset],
                                -data[curveIndex + 2 + offset],
                                data[curveIndex + 3 + offset]
                            ));
                            track.Rotations.Add(new ImportedKeyframe<Vector3>(time, value));
                        }
                        curveIndex += 4;
                        break;
                    case 3:
                        if ((uint)(curveIndex + 2 + offset) < (uint)data.Length)
                        {
                            track.Scalings.Add(new ImportedKeyframe<Vector3>(time, new Vector3
                            (
                                data[curveIndex + offset],
                                data[curveIndex + 1 + offset],
                                data[curveIndex + 2 + offset]
                            )));
                        }
                        curveIndex += 3;
                        break;
                    case 4:
                        if ((uint)(curveIndex + 2 + offset) < (uint)data.Length)
                        {
                            var qU = HumanoidMuscleDecoder.UnityEulerDegreesToQuaternion(
                                data[curveIndex + offset],
                                data[curveIndex + 1 + offset],
                                data[curveIndex + 2 + offset],
                                rotationOrder: 4);
                            var qF = new Quaternion(qU.X, -qU.Y, -qU.Z, qU.W);
                            track.Rotations.Add(new ImportedKeyframe<Vector3>(time, QuaternionToEuler(qF)));
                        }
                        curveIndex += 3;
                        break;
                    default:
                        curveIndex++;
                        break;
                }
            }
            else if (binding.typeID == ClassIDType.GameObject && binding.attribute == 2086281974)
            {
                var path = FixBonePath(animationClip, GetPathFromHash(binding.path));
                var track = iAnim.FindTrack(path);
                int idxVal = curveIndex++ + offset;
                if ((uint)idxVal < (uint)data.Length)
                {
                    track.Visibilities.Add(new ImportedKeyframe<float>(time, data[idxVal] >= 0.5f ? 1.0f : 0.0f, isStepped: true));
                }
            }
            else
            {
                curveIndex++;
            }
        }

        private static void FinalizeAnimationTracks(ImportedKeyframedAnimation iAnim)
        {
            if (iAnim.TrackList == null)
            {
                return;
            }

            for (int i = 0; i < iAnim.TrackList.Count; i++)
            {
                var track = iAnim.TrackList[i];
                SortAndDeduplicateVector3Keyframes(track.Translations);
                SortAndDeduplicateVector3Keyframes(track.Rotations);
                SortAndDeduplicateVector3Keyframes(track.Scalings);
                SortAndDeduplicateFloatKeyframes(track.Visibilities);

                if (track.BlendShapes != null)
                {
                    for (int b = 0; b < track.BlendShapes.Count; b++)
                    {
                        SortAndDeduplicateFloatKeyframes(track.BlendShapes[b].Keyframes);
                    }
                }

                HumanoidMuscleDecoder.UnwrapEulerKeyframes(track.Rotations);
            }
        }

        private static void SortAndDeduplicateVector3Keyframes(List<ImportedKeyframe<Vector3>> keyframes)
        {
            if (keyframes == null || keyframes.Count == 0)
            {
                return;
            }

            keyframes.RemoveAll(k => !float.IsFinite(k.time) || !float.IsFinite(k.value.X) || !float.IsFinite(k.value.Y) || !float.IsFinite(k.value.Z));
            if (keyframes.Count <= 1)
            {
                if (keyframes.Count == 1 && keyframes[0].time < 0f)
                {
                    keyframes[0].time = 0f;
                }
                return;
            }

            keyframes.Sort((a, b) => a.time.CompareTo(b.time));
            int write = 0;
            for (int i = 0; i < keyframes.Count; i++)
            {
                var k = keyframes[i];
                if (k.time < 0f) k.time = 0f;
                if (write > 0 && MathF.Abs(k.time - keyframes[write - 1].time) <= 1e-6f)
                {
                    keyframes[write - 1] = k;
                }
                else
                {
                    keyframes[write++] = k;
                }
            }
            if (write < keyframes.Count)
            {
                keyframes.RemoveRange(write, keyframes.Count - write);
            }
        }

        private static void SortAndDeduplicateFloatKeyframes(List<ImportedKeyframe<float>> keyframes)
        {
            if (keyframes == null || keyframes.Count == 0)
            {
                return;
            }

            keyframes.RemoveAll(k => !float.IsFinite(k.time) || !float.IsFinite(k.value));
            if (keyframes.Count <= 1)
            {
                if (keyframes.Count == 1 && keyframes[0].time < 0f)
                {
                    keyframes[0].time = 0f;
                }
                return;
            }

            keyframes.Sort((a, b) => a.time.CompareTo(b.time));
            int write = 0;
            for (int i = 0; i < keyframes.Count; i++)
            {
                var k = keyframes[i];
                if (k.time < 0f) k.time = 0f;
                if (write > 0 && MathF.Abs(k.time - keyframes[write - 1].time) <= 1e-6f)
                {
                    keyframes[write - 1] = k;
                }
                else
                {
                    keyframes[write++] = k;
                }
            }
            if (write < keyframes.Count)
            {
                keyframes.RemoveRange(write, keyframes.Count - write);
            }
        }

        private bool TryResolveBonePathFromHash(uint hash, [NotNullWhen(true)] out string? resolvedPath)
        {
            resolvedPath = null;
            if (hash == 0)
            {
                return false;
            }

            if (bonePathHash.TryGetValue(hash, out var boneName) && !string.IsNullOrEmpty(boneName))
            {
                var frame = RootFrame?.FindFrameByPath(boneName);
                resolvedPath = frame?.Path ?? boneName;
                return !string.IsNullOrEmpty(resolvedPath);
            }

            if (avatar != null)
            {
                boneName = avatar.FindBonePath(hash);
                if (!string.IsNullOrEmpty(boneName))
                {
                    var frame = RootFrame?.FindFrameByPath(boneName);
                    resolvedPath = frame?.Path ?? boneName;
                    return !string.IsNullOrEmpty(resolvedPath);
                }
            }

            return false;
        }

        private string GetPathFromHash(uint hash)
        {
            if (hash == 0)
            {
                return !string.IsNullOrEmpty(animatorRootPath)
                    ? animatorRootPath
                    : (RootFrame?.Path ?? string.Empty);
            }
            if (bonePathHash.TryGetValue(hash, out var boneName))
            {
                return boneName;
            }
            if (avatar != null)
            {
                boneName = avatar.FindBonePath(hash);
                if (boneName != null)
                {
                    return boneName;
                }
            }
            return $"Bone_{hash}";
        }

        private void CreateBonePathHash(Transform? m_Transform)
        {
            CreateBonePathHash(m_Transform, new HashSet<Transform>());
        }

        private void CreateBonePathHash(Transform? m_Transform, HashSet<Transform> visited)
        {
            if (m_Transform == null || !visited.Add(m_Transform))
            {
                return;
            }
            var fullFramePath = GetTransformPath(m_Transform);
            var fatherPath = GetTransformPathByFather(m_Transform);
            var canonicalPath = !string.IsNullOrEmpty(fullFramePath) ? fullFramePath : fatherPath;

            RegisterPathAndSuffixHashes(fatherPath, canonicalPath, overwriteExact: true);
            if (!string.IsNullOrEmpty(fullFramePath) && !string.Equals(fullFramePath, fatherPath, StringComparison.Ordinal))
            {
                RegisterPathAndSuffixHashes(fullFramePath, canonicalPath, overwriteExact: true);
            }

            foreach (var pptr in m_Transform.m_Children)
            {
                if (pptr.TryGet(out var child))
                    CreateBonePathHash(child, visited);
            }
        }

        private void RegisterPathAndSuffixHashes(string? pathToHash, string canonicalFramePath, bool overwriteExact)
        {
            if (string.IsNullOrEmpty(pathToHash) || string.IsNullOrEmpty(canonicalFramePath))
            {
                return;
            }

            var exactCrc = ComputeCrc32(pathToHash);
            if (overwriteExact || !bonePathHash.ContainsKey(exactCrc))
            {
                bonePathHash[exactCrc] = canonicalFramePath;
            }

            var suffix = pathToHash;
            int index;
            while ((index = suffix.IndexOf("/", StringComparison.Ordinal)) >= 0)
            {
                suffix = suffix.Substring(index + 1);
                if (string.IsNullOrEmpty(suffix))
                {
                    break;
                }
                var suffixCrc = ComputeCrc32(suffix);
                if (!bonePathHash.ContainsKey(suffixCrc))
                {
                    bonePathHash[suffixCrc] = canonicalFramePath;
                }
            }
        }

        private void DeoptimizeTransformHierarchy()
        {
            if (avatar == null)
                throw new Exception("Transform hierarchy has been optimized, but can't find Avatar to deoptimize.");

            var skeleton = avatar.m_Avatar.m_AvatarSkeleton;
            var skeletonPose = avatar.m_Avatar.m_DefaultPose?.m_X is { Length: > 0 }
                ? avatar.m_Avatar.m_DefaultPose
                : avatar.m_Avatar.m_AvatarSkeletonPose;
            var nodes = skeleton?.m_Node;

            if (skeleton == null || nodes == null || nodes.Length == 0 || skeleton.m_ID == null)
                return;

            BuildSkeletonFramesFromDefaultPose(skeleton, skeletonPose, fallbackMeshes: null, preserveExistingTransformFrames: false);
        }

        private void BuildSkeletonFramesFromDefaultPose(Skeleton skeleton, SkeletonPose? skeletonPose, IReadOnlyList<Mesh>? fallbackMeshes, bool preserveExistingTransformFrames)
        {
            var nodes = skeleton.m_Node;
            var skelIds = skeleton.m_ID;
            if (nodes == null || nodes.Length == 0 || skelIds == null || RootFrame == null)
                return;

            var skelCount = nodes.Length;
            bool hasDefaultPose = skeletonPose?.m_X != null && skeletonPose.m_X.Length >= skelCount;

            // Only fall back to m_BindPose decomposition when m_DefaultPose is absent/empty
            int[]? skelNodeToMeshBone = null;
            int[]? meshParentIndices = null;
            System.Numerics.Matrix4x4[]? bindPoseInverses = null;
            System.Numerics.Matrix4x4[]? bindPoses = null;
            bool useBindPoseFallback = false;

            if (!hasDefaultPose)
            {
                Mesh? fallbackMesh = null;
                int bestBoneCount = 0;
                if (fallbackMeshes != null)
                {
                    for (int i = 0; i < fallbackMeshes.Count; i++)
                    {
                        var m = fallbackMeshes[i];
                        if (m == null) continue;
                        m.EnsureProcessed();
                        if (m.m_BoneNameHashes != null && m.m_BindPose != null
                            && m.m_BoneNameHashes.Length == m.m_BindPose.Length
                            && m.m_BoneNameHashes.Length > bestBoneCount)
                        {
                            bestBoneCount = m.m_BoneNameHashes.Length;
                            fallbackMesh = m;
                        }
                    }
                }

                if (fallbackMesh != null && fallbackMesh.m_BindPose != null && fallbackMesh.m_BoneNameHashes != null && bestBoneCount > 0)
                {
                    skelNodeToMeshBone = new int[skelCount];
                    for (int i = 0; i < skelCount; i++) skelNodeToMeshBone[i] = -1;

                    var meshBoneHashToIdx = new Dictionary<uint, int>(bestBoneCount);
                    for (int j = 0; j < bestBoneCount; j++)
                    {
                        meshBoneHashToIdx[fallbackMesh.m_BoneNameHashes[j]] = j;
                    }

                    var meshBoneToSkelNode = new int[bestBoneCount];
                    for (int j = 0; j < bestBoneCount; j++) meshBoneToSkelNode[j] = -1;

                    for (int i = 0; i < skelCount && i < skelIds.Length; i++)
                    {
                        if (meshBoneHashToIdx.TryGetValue(skelIds[i], out int mbIdx))
                        {
                            skelNodeToMeshBone[i] = mbIdx;
                            meshBoneToSkelNode[mbIdx] = i;
                        }
                    }

                    meshParentIndices = new int[bestBoneCount];
                    for (int mb = 0; mb < bestBoneCount; mb++)
                    {
                        meshParentIndices[mb] = -1;
                        int skelIdx = meshBoneToSkelNode[mb];
                        if (skelIdx < 0) continue;

                        int parentId = nodes[skelIdx].m_ParentId;
                        if (parentId >= 0 && parentId < skelCount && skelNodeToMeshBone[parentId] >= 0)
                        {
                            meshParentIndices[mb] = skelNodeToMeshBone[parentId];
                        }
                    }

                    bindPoseInverses = new System.Numerics.Matrix4x4[bestBoneCount];
                    bindPoses = new System.Numerics.Matrix4x4[bestBoneCount];
                    useBindPoseFallback = true;
                    for (int j = 0; j < bestBoneCount; j++)
                    {
                        var bp = ToNumericsMatrix(fallbackMesh.m_BindPose[j]);
                        bindPoseInverses[j] = bp;
                        bindPoses[j] = System.Numerics.Matrix4x4.Invert(bp, out var inv)
                            ? inv
                            : System.Numerics.Matrix4x4.Identity;
                    }
                }
            }

            var existingTransformFrames = new HashSet<ImportedFrame>(transformDictionary.Values);
            var frames = new ImportedFrame[skelCount];
            for (int i = 0; i < skelCount; i++)
            {
                int parentId = nodes[i].m_ParentId;
                uint nodeId = i < skelIds.Length ? skelIds[i] : (uint)i;
                var fullPath = avatar?.FindBonePath(nodeId);

                // Root node (m_ParentId < 0 with empty bone path / ID == 0) represents RootFrame itself
                if (parentId < 0 && string.IsNullOrEmpty(fullPath))
                {
                    frames[i] = RootFrame;
                    bonePathHash[nodeId] = RootFrame.Path;
                    continue;
                }

                string name;
                if (string.IsNullOrEmpty(fullPath))
                {
                    name = $"Bone_{nodeId}";
                }
                else
                {
                    var lastSlash = fullPath.LastIndexOf('/');
                    name = lastSlash >= 0 ? fullPath.Substring(lastSlash + 1) : fullPath;
                }

                var xform = (skeletonPose?.m_X != null && i < skeletonPose.m_X.Length) ? skeletonPose.m_X[i] : null;
                var tVec = xform?.t ?? Vector3.Zero;
                var qQuat = xform?.q ?? new Quaternion(0, 0, 0, 1);
                var sVec = xform?.s ?? Vector3.One;

                if (useBindPoseFallback && skelNodeToMeshBone != null && meshParentIndices != null && bindPoses != null && bindPoseInverses != null)
                {
                    int mb = skelNodeToMeshBone[i];
                    if (mb >= 0)
                    {
                        int pMb = meshParentIndices[mb];
                        var localMat = pMb >= 0 ? bindPoses[mb] * bindPoseInverses[pMb] : bindPoses[mb];
                        if (System.Numerics.Matrix4x4.Decompose(localMat, out var s, out var q, out var t))
                        {
                            tVec = new Vector3(t.X, t.Y, t.Z);
                            qQuat = new Quaternion(q.X, q.Y, q.Z, q.W);
                            sVec = new Vector3(s.X, s.Y, s.Z);
                        }
                    }
                }

                ImportedFrame parentFrame = (parentId >= 0 && parentId < i && frames[parentId] != null)
                    ? frames[parentId]
                    : RootFrame;

                ImportedFrame? frame = null;
                if (!string.IsNullOrEmpty(fullPath))
                {
                    frame = RootFrame.FindRelativeFrameWithPath(fullPath) ?? RootFrame.FindFrameByPath(fullPath);
                }
                frame ??= parentFrame.FindChild(name, recursive: false) ?? RootFrame.FindChild(name, recursive: false);

                if (frame != null && !ReferenceEquals(frame, RootFrame))
                {
                    // Preserve real scene Transform pose & parenting when building Avatar fallback frames on an unoptimized hierarchy.
                    // For DeoptimizeTransformHierarchy (m_HasTransformHierarchy == false), exposed transforms were flattened under RootFrame
                    // by Unity and MUST be reparented back into the Avatar skeleton.
                    if (!preserveExistingTransformFrames || !existingTransformFrames.Contains(frame))
                    {
                        SetFrame(frame, tVec, qQuat, sVec);
                        if (!ReferenceEquals(frame.Parent, parentFrame))
                        {
                            parentFrame.AddChild(frame);
                        }
                    }
                }
                else
                {
                    frame = CreateFrame(name, tVec, qQuat, sVec);
                    parentFrame.AddChild(frame);
                }

                frames[i] = frame;
            }

            // Connect any out-of-order parent edges and register bone path hashes (full path + relative suffixes)
            for (int i = 0; i < skelCount; i++)
            {
                var frame = frames[i];
                if (frame == null || ReferenceEquals(frame, RootFrame))
                {
                    continue;
                }

                int parentId = nodes[i].m_ParentId;
                var parentFrame = (parentId >= 0 && parentId < frames.Length && frames[parentId] != null && parentId != i)
                    ? frames[parentId]
                    : RootFrame;
                if (frame.Parent == null)
                {
                    parentFrame.AddChild(frame);
                }

                if (i < skelIds.Length)
                {
                    var fullFramePath = frame.Path;
                    bonePathHash[skelIds[i]] = fullFramePath;
                    RegisterPathAndSuffixHashes(fullFramePath, fullFramePath, overwriteExact: true);
                }
            }

            // Register additional Avatar m_TOS hashes (e.g. non-skeleton transform paths used by animation bindings)
            if (avatar?.m_TOS != null)
            {
                for (int i = 0; i < avatar.m_TOS.Length; i++)
                {
                    var entry = avatar.m_TOS[i];
                    if (entry.Key == 0 || string.IsNullOrEmpty(entry.Value) || bonePathHash.ContainsKey(entry.Key))
                    {
                        continue;
                    }

                    var matched = RootFrame.FindRelativeFrameWithPath(entry.Value) ?? RootFrame.FindFrameByPath(entry.Value);
                    if (matched != null)
                    {
                        bonePathHash[entry.Key] = matched.Path;
                    }
                }
            }
        }

        private string? GetPathByChannelName(string channelName)
        {
            foreach (var morph in MorphList)
            {
                foreach (var channel in morph.Channels)
                {
                    if (channel.Name == channelName)
                    {
                        return morph.Path;
                    }
                }
            }
            return null;
        }

        private string? GetChannelNameFromHash(uint attribute)
        {
            if (morphChannelNames.TryGetValue(attribute, out var name))
            {
                return name;
            }
            else
            {
                return null;
            }
        }

        private Avatar? FindAvatar(GameObject? go)
        {
            if (go == null) return null;
            if (go.m_Animator != null && go.m_Animator.m_Avatar != null && go.m_Animator.m_Avatar.TryGet(out var av1))
                return av1;

            var trans = go.m_Transform;
            if (trans != null)
            {
                var visited = new HashSet<Transform>();
                var queue = new Queue<Transform>();
                queue.Enqueue(trans);
                visited.Add(trans);
                while (queue.Count > 0)
                {
                    var curr = queue.Dequeue();
                    if (curr.m_GameObject.TryGet(out var childGo) && childGo.m_Animator != null)
                    {
                        if (childGo.m_Animator.m_Avatar != null && childGo.m_Animator.m_Avatar.TryGet(out var av2))
                            return av2;
                    }
                    foreach (var childPtr in curr.m_Children)
                    {
                        if (childPtr.TryGet(out var childTrans) && visited.Add(childTrans))
                            queue.Enqueue(childTrans);
                    }
                }
            }

            if (go.assetsFile?.assetsManager != null)
            {
                // Match ONLY via genuine skeleton bone-hash overlap or explicit non-contradicting name match.
                // NEVER fall back blindly to an unrelated Avatar in the bundle.
                var cleanName = go.m_Name.Replace("_Model", "").Replace("Model", "").Trim();
                int nonZeroHierarchyHashes = 0;
                foreach (var key in bonePathHash.Keys)
                {
                    if (key != 0) nonZeroHierarchyHashes++;
                }

                Avatar? bestByBoneOverlap = null;
                int bestOverlap = 0;
                Avatar? bestByName = null;

                foreach (var sf in go.assetsFile.assetsManager.assetsFileList)
                {
                    foreach (var obj in sf.Objects)
                    {
                        if (obj is not Avatar candidate)
                        {
                            continue;
                        }

                        int overlap = CountAvatarBoneHashOverlap(candidate);
                        if (overlap > bestOverlap)
                        {
                            bestOverlap = overlap;
                            bestByBoneOverlap = candidate;
                        }

                        if (bestByName == null
                            && cleanName.Length >= 3
                            && IsStrongAvatarNameMatch(candidate.m_Name, cleanName)
                            && (nonZeroHierarchyHashes == 0 || overlap > 0))
                        {
                            bestByName = candidate;
                        }
                    }
                }

                int minRequiredOverlap = nonZeroHierarchyHashes >= 3 ? 3 : Math.Max(1, nonZeroHierarchyHashes);
                if (bestByBoneOverlap != null && bestOverlap >= minRequiredOverlap)
                {
                    return bestByBoneOverlap;
                }

                if (bestByName != null)
                {
                    return bestByName;
                }
            }

            return null;
        }

        private int CountAvatarBoneHashOverlap(Avatar candidate)
        {
            if (bonePathHash.Count == 0)
            {
                return 0;
            }

            int overlap = 0;
            var counted = new HashSet<uint>();
            var skelIds = candidate.m_Avatar?.m_AvatarSkeleton?.m_ID;
            if (skelIds != null)
            {
                for (int i = 0; i < skelIds.Length; i++)
                {
                    uint id = skelIds[i];
                    if (id != 0 && counted.Add(id) && bonePathHash.ContainsKey(id))
                    {
                        overlap++;
                    }
                }
            }

            if (candidate.m_TOS != null)
            {
                for (int i = 0; i < candidate.m_TOS.Length; i++)
                {
                    uint id = candidate.m_TOS[i].Key;
                    if (id != 0 && counted.Add(id) && bonePathHash.ContainsKey(id))
                    {
                        overlap++;
                    }
                }
            }

            return overlap;
        }

        private static bool IsStrongAvatarNameMatch(string? avatarName, string cleanName)
        {
            if (string.IsNullOrWhiteSpace(avatarName))
            {
                return false;
            }
            var trimmed = avatarName.Trim();
            if (string.Equals(trimmed, cleanName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (trimmed.EndsWith("Avatar", StringComparison.OrdinalIgnoreCase))
            {
                var stem = trimmed.Substring(0, trimmed.Length - "Avatar".Length).TrimEnd('_', ' ', '-');
                if (string.Equals(stem, cleanName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void ReadHumanoidMuscleCurves(ImportedKeyframedAnimation iAnim, AnimationClip animationClip)
        {
            if (avatar == null)
            {
                return;
            }

            var decodedTracks = HumanoidMuscleDecoder.DecodeHumanoidClip(animationClip, avatar);
            if (decodedTracks.Count == 0)
            {
                return;
            }

            for (int i = 0; i < decodedTracks.Count; i++)
            {
                var decoded = decodedTracks[i];
                if (string.IsNullOrEmpty(decoded.BonePath))
                {
                    continue;
                }

                string? resolvedPath = null;
                var skelIds = avatar.m_Avatar?.m_AvatarSkeleton?.m_ID;
                if (skelIds != null && decoded.AvatarNodeIndex >= 0 && decoded.AvatarNodeIndex < skelIds.Length)
                {
                    if (bonePathHash.TryGetValue(skelIds[decoded.AvatarNodeIndex], out var hashedPath)
                        && !string.IsNullOrEmpty(hashedPath))
                    {
                        resolvedPath = FixBonePath(animationClip, hashedPath);
                    }
                }

                string fixedPath = resolvedPath ?? FixBonePath(animationClip, decoded.BonePath) ?? decoded.BonePath;
                var track = iAnim.FindTrack(fixedPath);

                if (track.Translations.Count == 0 && decoded.Translations.Count > 0)
                {
                    for (int k = 0; k < decoded.Translations.Count; k++)
                    {
                        var kf = decoded.Translations[k];
                        track.Translations.Add(new ImportedKeyframe<Vector3>(
                            kf.time,
                            new Vector3(-kf.value.X, kf.value.Y, kf.value.Z),
                            kf.isStepped));
                    }
                }

                if (track.Rotations.Count == 0 && decoded.Rotations.Count > 0)
                {
                    for (int k = 0; k < decoded.Rotations.Count; k++)
                    {
                        var kf = decoded.Rotations[k];
                        var qFbx = new Quaternion(kf.value.X, -kf.value.Y, -kf.value.Z, kf.value.W);
                        track.Rotations.Add(new ImportedKeyframe<Vector3>(
                            kf.time,
                            QuaternionToEuler(qFbx),
                            kf.isStepped));
                    }
                }
            }
        }
    }
}
