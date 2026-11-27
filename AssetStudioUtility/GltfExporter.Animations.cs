using SkiaSharp;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AssetStudio
{
    public sealed partial class GltfExporter
    {
        // ------------------------------------------------------------------
        // Animations (TRS + Shortest-Arc Quaternions + Morph Weights)
        // ------------------------------------------------------------------

        private void ExportAnimation(ImportedKeyframedAnimation? anim)
        {
            if (anim == null || anim.TrackList == null || anim.TrackList.Count == 0)
            {
                return;
            }

            var gltfAnim = new GltfAnimation
            {
                Name = string.IsNullOrEmpty(anim.Name) ? $"Animation_{_animations.Count}" : anim.Name
            };

            // Group morph weight curves by resolved mesh node so each morphed mesh gets one combined weights channel.
            var morphCurvesByMeshNode = new Dictionary<int, Dictionary<int, (List<ImportedKeyframe<float>> Keys, float FullWeight)>>();
            var usedTrsTargets = new HashSet<(int Node, string Path)>();

            for (int tIdx = 0; tIdx < anim.TrackList.Count; tIdx++)
            {
                var track = anim.TrackList[tIdx];
                if (track == null || track.Path == null)
                {
                    continue;
                }

                int nodeIdx;
                bool hasResolvedTrackNode = TryResolveNodeIndex(track.Path, out nodeIdx)
                    || (string.IsNullOrEmpty(NormalizeFramePath(track.Path)) && _sceneRootNodes.Count > 0 && (nodeIdx = _sceneRootNodes[0]) >= 0);
                if (hasResolvedTrackNode)
                {
                    if (track.Translations != null && track.Translations.Count > 0 && usedTrsTargets.Add((nodeIdx, "translation")))
                    {
                        AddVec3Channel(gltfAnim, nodeIdx, "translation", track.Translations, _scaleFactor);
                    }

                    if (track.Rotations != null && track.Rotations.Count > 0 && usedTrsTargets.Add((nodeIdx, "rotation")))
                    {
                        AddRotationChannel(gltfAnim, nodeIdx, track.Rotations);
                    }

                    if (track.Scalings != null && track.Scalings.Count > 0 && usedTrsTargets.Add((nodeIdx, "scale")))
                    {
                        AddVec3Channel(gltfAnim, nodeIdx, "scale", track.Scalings, 1f);
                    }
                }

                if (_exportBlendShape && track.BlendShapes != null && track.BlendShapes.Count > 0)
                {
                    var normTrackPath = NormalizeFramePath(track.Path);
                    for (int b = 0; b < track.BlendShapes.Count; b++)
                    {
                        var bs = track.BlendShapes[b];
                        if (bs == null || bs.Keyframes == null || bs.Keyframes.Count == 0)
                        {
                            continue;
                        }

                        if (TryResolveMorphChannel(normTrackPath, bs.ChannelName, out int meshNodeIdx, out int targetIdx, out float fullWeight))
                        {
                            if (!morphCurvesByMeshNode.TryGetValue(meshNodeIdx, out var channelMap))
                            {
                                channelMap = new Dictionary<int, (List<ImportedKeyframe<float>>, float)>();
                                morphCurvesByMeshNode[meshNodeIdx] = channelMap;
                            }
                            channelMap[targetIdx] = (bs.Keyframes, fullWeight);
                        }
                    }
                }
            }

            foreach (var kv in morphCurvesByMeshNode)
            {
                int meshNodeIdx = kv.Key;
                if (!_morphTargetsByNode.TryGetValue(meshNodeIdx, out var morphInfo) || morphInfo.TargetCount <= 0)
                {
                    continue;
                }

                AddMorphWeightsChannel(gltfAnim, meshNodeIdx, morphInfo.TargetCount, kv.Value);
            }

            if (gltfAnim.Channels.Count > 0)
            {
                _animations.Add(gltfAnim);
            }
        }

        private void AddVec3Channel(
            GltfAnimation gltfAnim,
            int nodeIdx,
            string path,
            List<ImportedKeyframe<Vector3>> rawKeyframes,
            float valueScale)
        {
            var prepared = PrepareStrictMonotonicKeyframes(rawKeyframes);
            if (prepared.Count == 0)
            {
                return;
            }

            bool allStepped = true;
            var times = new float[prepared.Count];
            var values = new System.Numerics.Vector3[prepared.Count];

            for (int i = 0; i < prepared.Count; i++)
            {
                var kf = prepared[i];
                if (!kf.isStepped) allStepped = false;
                times[i] = kf.time;
                if (path == "scale")
                {
                    values[i] = new System.Numerics.Vector3(
                        SanitizeScaleFloat(kf.value.X),
                        SanitizeScaleFloat(kf.value.Y),
                        SanitizeScaleFloat(kf.value.Z));
                }
                else
                {
                    values[i] = new System.Numerics.Vector3(
                        SanitizeFloat(kf.value.X * valueScale),
                        SanitizeFloat(kf.value.Y * valueScale),
                        SanitizeFloat(kf.value.Z * valueScale));
                }
            }

            int inputAcc = _buffer.AddScalarFloatAccessor(times, includeMinMax: true, target: null);
            int outputAcc = _buffer.AddVec3Accessor(values, includeMinMax: false, target: null);

            int samplerIdx = gltfAnim.Samplers.Count;
            gltfAnim.Samplers.Add(new GltfAnimationSampler
            {
                Input = inputAcc,
                Output = outputAcc,
                Interpolation = allStepped ? "STEP" : "LINEAR"
            });
            gltfAnim.Channels.Add(new GltfAnimationChannel
            {
                Sampler = samplerIdx,
                TargetNode = nodeIdx,
                TargetPath = path
            });
        }

        private void AddRotationChannel(
            GltfAnimation gltfAnim,
            int nodeIdx,
            List<ImportedKeyframe<Vector3>> rawKeyframes)
        {
            var prepared = PrepareStrictMonotonicKeyframes(rawKeyframes);
            if (prepared.Count == 0)
            {
                return;
            }

            bool allStepped = true;
            var times = new float[prepared.Count];
            var values = new System.Numerics.Vector4[prepared.Count];
            System.Numerics.Quaternion prevQ = System.Numerics.Quaternion.Identity;

            for (int i = 0; i < prepared.Count; i++)
            {
                var kf = prepared[i];
                if (!kf.isStepped) allStepped = false;
                times[i] = kf.time;

                var q = EulerDegreesToQuaternion(kf.value);
                if (i > 0 && System.Numerics.Quaternion.Dot(prevQ, q) < 0f)
                {
                    // Ensure shortest-arc slerp continuity across keyframes.
                    q = new System.Numerics.Quaternion(-q.X, -q.Y, -q.Z, -q.W);
                }
                prevQ = q;
                values[i] = new System.Numerics.Vector4(q.X, q.Y, q.Z, q.W);
            }

            int inputAcc = _buffer.AddScalarFloatAccessor(times, includeMinMax: true, target: null);
            int outputAcc = _buffer.AddVec4Accessor(values, target: null);

            int samplerIdx = gltfAnim.Samplers.Count;
            gltfAnim.Samplers.Add(new GltfAnimationSampler
            {
                Input = inputAcc,
                Output = outputAcc,
                Interpolation = allStepped ? "STEP" : "LINEAR"
            });
            gltfAnim.Channels.Add(new GltfAnimationChannel
            {
                Sampler = samplerIdx,
                TargetNode = nodeIdx,
                TargetPath = "rotation"
            });
        }

        private void AddMorphWeightsChannel(
            GltfAnimation gltfAnim,
            int meshNodeIdx,
            int targetCount,
            Dictionary<int, (List<ImportedKeyframe<float>> Keys, float FullWeight)> animatedChannels)
        {
            var sortedCurves = new Dictionary<int, (List<ImportedKeyframe<float>> Keys, float FullWeight)>(animatedChannels.Count);
            var rawTimes = new List<float>();

            foreach (var kv in animatedChannels)
            {
                var cleaned = PrepareStrictMonotonicKeyframes(kv.Value.Keys);
                if (cleaned.Count == 0)
                {
                    continue;
                }
                sortedCurves[kv.Key] = (cleaned, kv.Value.FullWeight);
                for (int i = 0; i < cleaned.Count; i++)
                {
                    rawTimes.Add(cleaned[i].time);
                }
            }

            if (rawTimes.Count == 0)
            {
                return;
            }

            rawTimes.Sort();
            var mergedTimes = new List<float>(rawTimes.Count);
            for (int i = 0; i < rawTimes.Count; i++)
            {
                float t = rawTimes[i];
                if (mergedTimes.Count == 0 || t > mergedTimes[mergedTimes.Count - 1] + 1e-6f)
                {
                    mergedTimes.Add(t);
                }
            }

            if (mergedTimes.Count == 0)
            {
                return;
            }

            var timeArray = mergedTimes.ToArray();
            var weightOutput = new float[checked(timeArray.Length * targetCount)];

            for (int k = 0; k < timeArray.Length; k++)
            {
                float t = timeArray[k];
                int rowOffset = k * targetCount;

                foreach (var kv in sortedCurves)
                {
                    int targetIdx = kv.Key;
                    if ((uint)targetIdx >= (uint)targetCount)
                    {
                        continue;
                    }

                    var (keys, fullWeight) = kv.Value;
                    float rawVal = SampleFloatCurve(keys, t);
                    float normalized = fullWeight > 1e-4f ? rawVal / fullWeight : rawVal;
                    weightOutput[rowOffset + targetIdx] = SanitizeFloat(normalized);
                }
            }

            int inputAcc = _buffer.AddScalarFloatAccessor(timeArray, includeMinMax: true, target: null);
            int outputAcc = _buffer.AddScalarFloatAccessor(weightOutput, includeMinMax: false, target: null);

            int samplerIdx = gltfAnim.Samplers.Count;
            gltfAnim.Samplers.Add(new GltfAnimationSampler
            {
                Input = inputAcc,
                Output = outputAcc,
                Interpolation = "LINEAR"
            });
            gltfAnim.Channels.Add(new GltfAnimationChannel
            {
                Sampler = samplerIdx,
                TargetNode = meshNodeIdx,
                TargetPath = "weights"
            });
        }

        private bool TryResolveMorphChannel(
            string normTrackPath,
            string? rawChannelName,
            out int meshNodeIdx,
            out int targetIndex,
            out float fullWeight)
        {
            meshNodeIdx = -1;
            targetIndex = -1;
            fullWeight = 100f;

            if (string.IsNullOrEmpty(rawChannelName))
            {
                return false;
            }

            const string bsPrefix = "blendShape.";
            string cleanName = rawChannelName.StartsWith(bsPrefix, StringComparison.OrdinalIgnoreCase)
                ? rawChannelName.Substring(bsPrefix.Length)
                : rawChannelName;

            // 1. Direct normalized mesh path lookup
            if (!string.IsNullOrEmpty(normTrackPath) && _morphTargetsByPath.TryGetValue(normTrackPath, out var pathInfo))
            {
                if (pathInfo.TryFindTarget(cleanName, rawChannelName, out targetIndex, out fullWeight))
                {
                    meshNodeIdx = pathInfo.MeshNodeIndex;
                    return true;
                }
            }

            // 2. Suffix path lookup -> canonical path
            if (!string.IsNullOrEmpty(normTrackPath)
                && _uniqueSuffixToPath.TryGetValue(normTrackPath, out var canonical)
                && canonical != null
                && _morphTargetsByPath.TryGetValue(canonical, out var canonicalInfo))
            {
                if (canonicalInfo.TryFindTarget(cleanName, rawChannelName, out targetIndex, out fullWeight))
                {
                    meshNodeIdx = canonicalInfo.MeshNodeIndex;
                    return true;
                }
            }

            // 3. Resolved node or its dedicated child mesh node
            if (TryResolveNodeIndex(normTrackPath, out int resolvedNode))
            {
                if (_morphTargetsByNode.TryGetValue(resolvedNode, out var nodeInfo)
                    && nodeInfo.TryFindTarget(cleanName, rawChannelName, out targetIndex, out fullWeight))
                {
                    meshNodeIdx = nodeInfo.MeshNodeIndex;
                    return true;
                }

                var children = _nodes[resolvedNode].Children;
                for (int c = 0; c < children.Count; c++)
                {
                    if (_morphTargetsByNode.TryGetValue(children[c], out var childInfo)
                        && childInfo.TryFindTarget(cleanName, rawChannelName, out targetIndex, out fullWeight))
                    {
                        meshNodeIdx = childInfo.MeshNodeIndex;
                        return true;
                    }
                }
            }

            // 4. Unique channel-name fallback across exported morph meshes
            if (_uniqueMorphChannelLookup.TryGetValue(cleanName, out var uniqueMatch) && uniqueMatch.HasValue)
            {
                meshNodeIdx = uniqueMatch.Value.MeshNodeIndex;
                targetIndex = uniqueMatch.Value.TargetIndex;
                fullWeight = uniqueMatch.Value.FullWeight;
                return true;
            }

            return false;
        }

        private static List<ImportedKeyframe<T>> PrepareStrictMonotonicKeyframes<T>(List<ImportedKeyframe<T>> raw)
        {
            var result = new List<ImportedKeyframe<T>>(raw.Count);
            if (raw.Count == 0)
            {
                return result;
            }

            // Sanitize times (non-negative finite) and sort stably by time
            var sanitized = new List<(float Time, int OriginalOrder, ImportedKeyframe<T> Key)>(raw.Count);
            for (int i = 0; i < raw.Count; i++)
            {
                var k = raw[i];
                if (k == null) continue;
                float t = SanitizeFloat(k.time);
                if (t < 0f) t = 0f;
                sanitized.Add((t, i, k));
            }

            sanitized.Sort(static (a, b) =>
            {
                int cmp = a.Time.CompareTo(b.Time);
                return cmp != 0 ? cmp : a.OriginalOrder.CompareTo(b.OriginalOrder);
            });

            // Deduplicate equal or sub-epsilon timestamps (keeping the latest value at that timestamp)
            for (int i = 0; i < sanitized.Count; i++)
            {
                var item = sanitized[i];
                if (result.Count > 0 && item.Time <= result[result.Count - 1].time + 1e-6f)
                {
                    result[result.Count - 1] = new ImportedKeyframe<T>(result[result.Count - 1].time, item.Key.value, item.Key.isStepped);
                }
                else
                {
                    result.Add(new ImportedKeyframe<T>(item.Time, item.Key.value, item.Key.isStepped));
                }
            }

            return result;
        }

        private static float SampleFloatCurve(List<ImportedKeyframe<float>> keys, float time)
        {
            if (keys.Count == 0) return 0f;
            if (keys.Count == 1 || time <= keys[0].time) return SanitizeFloat(keys[0].value);
            if (time >= keys[keys.Count - 1].time) return SanitizeFloat(keys[keys.Count - 1].value);

            int lo = 0;
            int hi = keys.Count - 1;
            while (lo + 1 < hi)
            {
                int mid = (lo + hi) >> 1;
                if (keys[mid].time <= time) lo = mid;
                else hi = mid;
            }

            var k0 = keys[lo];
            var k1 = keys[hi];
            if (k0.isStepped || k1.time <= k0.time + 1e-7f)
            {
                return SanitizeFloat(k0.value);
            }

            float alpha = (time - k0.time) / (k1.time - k0.time);
            return SanitizeFloat(k0.value + (k1.value - k0.value) * alpha);
        }

    }
}
