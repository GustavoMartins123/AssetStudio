using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetStudio
{
    /// <summary>
    /// Shared Unity animation math & Humanoid MuscleClip decoder used by both
    /// <see cref="ModelConverter"/> (FBX/.anim export) and the 3D preview evaluator.
    /// </summary>
    public static class HumanoidMuscleDecoder
    {
        public const int MotionSlotOffset = 0;
        public const int RootSlotOffset = 7;
        public const int GoalSlotOffset = 14;
        public const int MuscleSlotOffset = 42;
        public const int ModernMuscleCount = 95;
        public const int LegacyMuscleCount = 92;

        public sealed class DecodedBoneTrack
        {
            public int AvatarNodeIndex { get; init; }
            public string BonePath { get; init; } = string.Empty;
            public List<ImportedKeyframe<Vector3>> Translations { get; } = new();
            public List<ImportedKeyframe<Quaternion>> Rotations { get; } = new();
        }

        public readonly struct HumanBodyBoneDef
        {
            public readonly int BodyIndex56;
            public readonly int BodyIndexLegacy; // -1 if bone did not exist before Unity 5.6 (UpperChest)
            public readonly int MuscleStart56;
            public readonly int[] DofAxes; // 0 = X (twist), 1 = Y (in-out/spread), 2 = Z (front-back/stretch)
            public readonly string UnityName;

            public HumanBodyBoneDef(int bodyIndex56, int bodyIndexLegacy, int muscleStart56, int[] dofAxes, string unityName)
            {
                BodyIndex56 = bodyIndex56;
                BodyIndexLegacy = bodyIndexLegacy;
                MuscleStart56 = muscleStart56;
                DofAxes = dofAxes;
                UnityName = unityName;
            }
        }

        // Exact Unity 5.6+ Human.m_HumanBoneIndex[0..24] and legacy < 5.6 [0..23] layout,
        // paired with exact Unity HumanTrait muscle offsets (0..54) and Mecanim DoF axes (Z=2, Y=1, X=0).
        public static readonly HumanBodyBoneDef[] BodyBoneDefs =
        {
            // Spine / Torso / Head (m0..m20)
            new(7,  7,  0,  new[] { 2, 1, 0 }, "Spine"),
            new(8,  8,  3,  new[] { 2, 1, 0 }, "Chest"),
            new(9,  -1, 6,  new[] { 2, 1, 0 }, "UpperChest"),
            new(10, 9,  9,  new[] { 2, 1, 0 }, "Neck"),
            new(11, 10, 12, new[] { 2, 1, 0 }, "Head"),
            new(22, 21, 15, new[] { 2, 1 },    "LeftEye"),
            new(23, 22, 17, new[] { 2, 1 },    "RightEye"),
            new(24, 23, 19, new[] { 2, 1 },    "Jaw"),

            // Left Leg (m21..m28)
            new(1,  1,  21, new[] { 2, 1, 0 }, "LeftUpperLeg"),
            new(3,  3,  24, new[] { 2, 0 },    "LeftLowerLeg"),
            new(5,  5,  26, new[] { 2, 1 },    "LeftFoot"),
            new(20, 19, 28, new[] { 2 },       "LeftToes"),

            // Right Leg (m29..m36)
            new(2,  2,  29, new[] { 2, 1, 0 }, "RightUpperLeg"),
            new(4,  4,  32, new[] { 2, 0 },    "RightLowerLeg"),
            new(6,  6,  34, new[] { 2, 1 },    "RightFoot"),
            new(21, 20, 36, new[] { 2 },       "RightToes"),

            // Left Arm (m37..m45)
            new(12, 11, 37, new[] { 2, 1 },    "LeftShoulder"),
            new(14, 13, 39, new[] { 2, 1, 0 }, "LeftUpperArm"),
            new(16, 15, 42, new[] { 2, 0 },    "LeftLowerArm"),
            new(18, 17, 44, new[] { 2, 1 },    "LeftHand"),

            // Right Arm (m46..m54)
            new(13, 12, 46, new[] { 2, 1 },    "RightShoulder"),
            new(15, 14, 48, new[] { 2, 1, 0 }, "RightUpperArm"),
            new(17, 16, 51, new[] { 2, 0 },    "RightLowerArm"),
            new(19, 18, 53, new[] { 2, 1 },    "RightHand"),
        };

        public static readonly string[] LeftHandBoneNames =
        {
            "Left Thumb Proximal", "Left Thumb Intermediate", "Left Thumb Distal",
            "Left Index Proximal", "Left Index Intermediate", "Left Index Distal",
            "Left Middle Proximal", "Left Middle Intermediate", "Left Middle Distal",
            "Left Ring Proximal", "Left Ring Intermediate", "Left Ring Distal",
            "Left Little Proximal", "Left Little Intermediate", "Left Little Distal",
        };

        public static readonly string[] RightHandBoneNames =
        {
            "Right Thumb Proximal", "Right Thumb Intermediate", "Right Thumb Distal",
            "Right Index Proximal", "Right Index Intermediate", "Right Index Distal",
            "Right Middle Proximal", "Right Middle Intermediate", "Right Middle Distal",
            "Right Ring Proximal", "Right Ring Intermediate", "Right Ring Distal",
            "Right Little Proximal", "Right Little Intermediate", "Right Little Distal",
        };

        private static readonly int[] FingerProximalDofAxes = { 2, 1 }; // Stretched (Z), Spread (Y)
        private static readonly int[] FingerHingeDofAxes = { 2 };       // Stretched (Z)

        /// <summary>
        /// Pre-indexed cubic Hermite evaluator for a Unity <see cref="StreamedClip"/>.
        /// Strictly skips sentinel frames 0 (-3.4e38) and Count-1 (+Infinity).
        /// </summary>
        public sealed class StreamedCurveEvaluator
        {
            public readonly struct Key
            {
                public readonly float Time;
                public readonly float Value;
                public readonly float C0;
                public readonly float C1;
                public readonly float C2;
                public readonly float C3;
                public readonly bool IsStepped;

                public Key(float time, StreamedClip.StreamedCurveKey raw)
                {
                    Time = time;
                    Value = raw.value;
                    var cf = raw.coeff;
                    if (cf != null && cf.Length >= 4)
                    {
                        C0 = cf[0];
                        C1 = cf[1];
                        C2 = cf[2];
                        C3 = cf[3];
                        IsStepped = C0 == 0f && C1 == 0f && C2 == 0f;
                    }
                    else
                    {
                        C0 = 0f;
                        C1 = 0f;
                        C2 = 0f;
                        C3 = raw.value;
                        IsStepped = true;
                    }
                }

                public float Evaluate(float dt)
                {
                    if (IsStepped || dt <= 0f)
                    {
                        return C3;
                    }
                    float v = ((C0 * dt + C1) * dt + C2) * dt + C3;
                    return float.IsFinite(v) ? v : C3;
                }
            }

            private readonly Dictionary<int, List<Key>> _curves = new();
            private readonly List<float> _interiorTimes = new();

            public IReadOnlyList<float> InteriorTimes => _interiorTimes;
            public bool HasAnyKeys => _curves.Count > 0;

            public StreamedCurveEvaluator(List<StreamedClip.StreamedFrame>? frames)
            {
                if (frames == null || frames.Count < 3)
                {
                    return;
                }

                // Frame 0 is sentinel (-3.4e38) and Frame Count-1 is sentinel (+Infinity)
                for (int fi = 1; fi < frames.Count - 1; fi++)
                {
                    var fr = frames[fi];
                    if (fr == null || !float.IsFinite(fr.time) || fr.time < -1e6f || fr.time > 1e6f)
                    {
                        continue;
                    }

                    float t = fr.time < 0f ? 0f : fr.time;
                    if (_interiorTimes.Count == 0 || Math.Abs(t - _interiorTimes[^1]) > 1e-6f)
                    {
                        _interiorTimes.Add(t);
                    }

                    var keyList = fr.keyList;
                    if (keyList == null || keyList.Length == 0)
                    {
                        continue;
                    }

                    for (int k = 0; k < keyList.Length; k++)
                    {
                        var rk = keyList[k];
                        if (rk == null || !float.IsFinite(rk.value))
                        {
                            continue;
                        }

                        if (!_curves.TryGetValue(rk.index, out var list))
                        {
                            list = new List<Key>();
                            _curves[rk.index] = list;
                        }

                        var entry = new Key(t, rk);
                        if (list.Count > 0 && Math.Abs(list[^1].Time - t) <= 1e-6f)
                        {
                            list[^1] = entry;
                        }
                        else
                        {
                            list.Add(entry);
                        }
                    }
                }
            }

            public bool TryGetCurveKeys(int curveIndex, out List<Key> keys)
            {
                return _curves.TryGetValue(curveIndex, out keys!);
            }

            public bool HasCurve(int curveIndex) => _curves.ContainsKey(curveIndex);

            public bool TryEvaluate(int curveIndex, float time, out float value, out bool isStepped)
            {
                if (!_curves.TryGetValue(curveIndex, out var keys) || keys.Count == 0)
                {
                    value = 0f;
                    isStepped = false;
                    return false;
                }

                if (keys.Count == 1 || time <= keys[0].Time)
                {
                    value = keys[0].Value;
                    isStepped = keys[0].IsStepped;
                    return true;
                }

                if (time >= keys[^1].Time)
                {
                    value = keys[^1].Value;
                    isStepped = keys[^1].IsStepped;
                    return true;
                }

                int seg = FindSegment(keys, time);
                var k0 = keys[seg];
                isStepped = k0.IsStepped;
                value = k0.Evaluate(time - k0.Time);
                return true;
            }

            public float EvaluateOrDefault(int curveIndex, float time, float defaultValue = 0f)
            {
                return TryEvaluate(curveIndex, time, out var v, out _) ? v : defaultValue;
            }

            private static int FindSegment(List<Key> keys, float time)
            {
                int lo = 0;
                int hi = keys.Count - 2;
                while (lo <= hi)
                {
                    int mid = (lo + hi) >>> 1;
                    if (keys[mid + 1].Time <= time)
                    {
                        lo = mid + 1;
                    }
                    else if (keys[mid].Time > time)
                    {
                        hi = mid - 1;
                    }
                    else
                    {
                        return mid;
                    }
                }
                return Math.Clamp(lo, 0, keys.Count - 2);
            }
        }

        /// <summary>
        /// Converts Unity Euler angles (degrees) into a Unity left-handed quaternion,
        /// respecting Unity's <c>RotationOrder</c> enum (default 4 = OrderZXY: qY * qX * qZ).
        /// </summary>
        public static Quaternion UnityEulerDegreesToQuaternion(float exDeg, float eyDeg, float ezDeg, int rotationOrder = 4)
        {
            const float deg2rad = (float)(Math.PI / 180.0);
            float hx = exDeg * deg2rad * 0.5f;
            float hy = eyDeg * deg2rad * 0.5f;
            float hz = ezDeg * deg2rad * 0.5f;

            var qx = new Quaternion(MathF.Sin(hx), 0f, 0f, MathF.Cos(hx));
            var qy = new Quaternion(0f, MathF.Sin(hy), 0f, MathF.Cos(hy));
            var qz = new Quaternion(0f, 0f, MathF.Sin(hz), MathF.Cos(hz));

            // Unity RotationOrder: 0=XYZ (qZ*qY*qX), 1=XZY (qY*qZ*qX), 2=YZX (qX*qZ*qY),
            // 3=YXZ (qZ*qX*qY), 4=ZXY (qY*qX*qZ - Unity default), 5=ZYX (qX*qY*qZ).
            Quaternion q = rotationOrder switch
            {
                0 => MultiplyQuat(MultiplyQuat(qz, qy), qx),
                1 => MultiplyQuat(MultiplyQuat(qy, qz), qx),
                2 => MultiplyQuat(MultiplyQuat(qx, qz), qy),
                3 => MultiplyQuat(MultiplyQuat(qz, qx), qy),
                5 => MultiplyQuat(MultiplyQuat(qx, qy), qz),
                _ => MultiplyQuat(MultiplyQuat(qy, qx), qz),
            };
            return NormalizeQuat(q);
        }

        /// <summary>
        /// Converts FBX right-handed eEulerXYZ (degrees, Rz * Ry * Rx) back to a Unity left-handed quaternion.
        /// Exact inverse of ModelConverter's LH-&gt;RH reflection + QuaternionToEuler.
        /// </summary>
        public static Quaternion FbxEulerXYZDegreesToUnityQuaternion(Vector3 fbxEulerDeg)
        {
            const float deg2rad = (float)(Math.PI / 180.0);
            float hx = fbxEulerDeg.X * deg2rad * 0.5f;
            float hy = fbxEulerDeg.Y * deg2rad * 0.5f;
            float hz = fbxEulerDeg.Z * deg2rad * 0.5f;

            var qx = new Quaternion(MathF.Sin(hx), 0f, 0f, MathF.Cos(hx));
            var qy = new Quaternion(0f, MathF.Sin(hy), 0f, MathF.Cos(hy));
            var qz = new Quaternion(0f, 0f, MathF.Sin(hz), MathF.Cos(hz));

            // FBX eEulerXYZ: column-vector R = Rz * Ry * Rx -> Hamilton quaternion qF = qz * qy * qx
            var qF = NormalizeQuat(MultiplyQuat(MultiplyQuat(qz, qy), qx));
            // Reflect RH -> Unity LH: (qF.X, -qF.Y, -qF.Z, qF.W)
            return NormalizeQuat(new Quaternion(qF.X, -qF.Y, -qF.Z, qF.W));
        }

        /// <summary>
        /// Computes the local Mecanim muscle quaternion in Unity ZYX DoF order:
        /// <c>qMuscle = qZ * qY * qX</c>, followed by <c>qLocal = preQ * qMuscle * Conjugate(postQ)</c>.
        /// </summary>
        public static Quaternion ComputeMecanimMuscleLocalRotation(
            Quaternion preQ,
            Quaternion postQ,
            float thetaXRad,
            float thetaYRad,
            float thetaZRad)
        {
            float hx = thetaXRad * 0.5f;
            float hy = thetaYRad * 0.5f;
            float hz = thetaZRad * 0.5f;

            var qx = new Quaternion(MathF.Sin(hx), 0f, 0f, MathF.Cos(hx));
            var qy = new Quaternion(0f, MathF.Sin(hy), 0f, MathF.Cos(hy));
            var qz = new Quaternion(0f, 0f, MathF.Sin(hz), MathF.Cos(hz));

            // Unity Mecanim Limit::ToQuat / ZYX2Q: qZ * qY * qX
            var qMuscle = MultiplyQuat(MultiplyQuat(qz, qy), qx);
            var preNorm = NormalizeQuat(preQ);
            var postInv = ConjugateQuat(NormalizeQuat(postQ));
            return NormalizeQuat(MultiplyQuat(MultiplyQuat(preNorm, qMuscle), postInv));
        }

        /// <summary>
        /// Continuous Euler unwrapper (EulerFilter) for FBX eEulerXYZ rotation tracks.
        /// For each frame k &gt; 0, evaluates both direct branch <c>e1</c> and exact gimbal-dual
        /// branch <c>e2 = (rx + 180, 180 - ry, rz + 180)</c>, unwrapping each axis by multiples of
        /// 360 degrees closest to the previous keyframe and picking the branch minimizing ||e - prev||^2.
        /// Eliminates all 180-degree gimbal branch flips and 360-degree wrap limb spins.
        /// </summary>
        public static void UnwrapEulerKeyframes(List<ImportedKeyframe<Vector3>>? rotations)
        {
            if (rotations == null || rotations.Count < 2)
            {
                return;
            }

            var prev = rotations[0].value;
            if (!IsFiniteVec(prev))
            {
                prev = Vector3.Zero;
                rotations[0].value = prev;
            }

            for (int i = 1; i < rotations.Count; i++)
            {
                var cur = rotations[i].value;
                if (!IsFiniteVec(cur))
                {
                    rotations[i].value = prev;
                    continue;
                }

                // Branch 1: direct wrap
                float x1 = UnwrapAngleDeg(cur.X, prev.X);
                float y1 = UnwrapAngleDeg(cur.Y, prev.Y);
                float z1 = UnwrapAngleDeg(cur.Z, prev.Z);
                float dx1 = x1 - prev.X, dy1 = y1 - prev.Y, dz1 = z1 - prev.Z;
                float dist1 = dx1 * dx1 + dy1 * dy1 + dz1 * dz1;

                // Branch 2: exact eEulerXYZ dual (Rx(x+180) * Ry(180-y) * Rz(z+180) == Rx(x)*Ry(y)*Rz(z))
                float x2 = UnwrapAngleDeg(cur.X + 180f, prev.X);
                float y2 = UnwrapAngleDeg(180f - cur.Y, prev.Y);
                float z2 = UnwrapAngleDeg(cur.Z + 180f, prev.Z);
                float dx2 = x2 - prev.X, dy2 = y2 - prev.Y, dz2 = z2 - prev.Z;
                float dist2 = dx2 * dx2 + dy2 * dy2 + dz2 * dz2;

                var best = dist2 < dist1 ? new Vector3(x2, y2, z2) : new Vector3(x1, y1, z1);
                rotations[i].value = best;
                prev = best;
            }
        }

        private static float UnwrapAngleDeg(float angle, float reference)
        {
            float delta = angle - reference;
            delta -= 360f * MathF.Round(delta / 360f);
            return reference + delta;
        }

        /// <summary>
        /// Decodes a Unity Humanoid <see cref="AnimationClip"/> against an <see cref="Avatar"/> into
        /// Unity left-handed local bone tracks (Pelvis Translation+Rotation and all animated body &amp; finger bone Rotations).
        /// </summary>
        public static List<DecodedBoneTrack> DecodeHumanoidClip(AnimationClip animationClip, Avatar avatar)
        {
            var result = new List<DecodedBoneTrack>();
            if (animationClip == null || avatar?.m_Avatar == null)
            {
                return result;
            }

            var avConst = avatar.m_Avatar;
            var human = avConst.m_Human;
            var avSkel = avConst.m_AvatarSkeleton;
            var hSkel = human?.m_Skeleton;
            var hIdxArr = avConst.m_HumanSkeletonIndexArray;
            var muscleClip = animationClip.m_MuscleClip;
            var innerClip = muscleClip?.m_Clip;
            var indexArray = muscleClip?.m_IndexArray;

            if (human == null
                || avSkel?.m_Node == null || avSkel.m_ID == null
                || hSkel?.m_Node == null || hSkel.m_AxesArray == null
                || human.m_HumanBoneIndex == null || human.m_HumanBoneIndex.Length == 0
                || hIdxArr == null || hIdxArr.Length == 0
                || muscleClip == null || innerClip == null
                || indexArray == null || indexArray.Length <= RootSlotOffset)
            {
                return result;
            }

            // Determine clip muscle layout (95 muscles in Unity 5.6+, 92 muscles in legacy Unity < 5.6 without UpperChest)
            bool isLegacyClip92 = IsLegacy92MuscleClip(animationClip, indexArray.Length);
            bool isLegacyAvatar24 = human.m_HumanBoneIndex.Length < 25;

            var streamedClip = innerClip.m_StreamedClip;
            var denseClip = innerClip.m_DenseClip;
            var constantClip = innerClip.m_ConstantClip;
            uint streamCount = streamedClip?.curveCount ?? 0;
            uint denseCount = denseClip?.m_CurveCount ?? 0;

            StreamedCurveEvaluator? streamedEval = null;
            if (streamedClip != null && streamCount > 0 && streamedClip.data != null && streamedClip.data.Length > 0)
            {
                try
                {
                    streamedEval = new StreamedCurveEvaluator(streamedClip.ReadData());
                }
                catch
                {
                    streamedEval = null;
                }
            }

            bool hasDense = denseClip != null && denseClip.m_FrameCount > 0 && denseCount > 0 && denseClip.m_SampleArray != null && denseClip.m_SampleArray.Length > 0;
            float clipSampleRate = animationClip.m_SampleRate > 0f && float.IsFinite(animationClip.m_SampleRate)
                ? animationClip.m_SampleRate
                : 30f;
            float denseSampleRate = hasDense && denseClip!.m_SampleRate > 0f && float.IsFinite(denseClip.m_SampleRate)
                ? denseClip.m_SampleRate
                : clipSampleRate;

            // Build sorted, finite sample timestamps covering the clip cleanly
            var sampleTimes = BuildSampleTimestamps(muscleClip, denseClip, hasDense, denseSampleRate, clipSampleRate, streamedEval);
            if (sampleTimes.Length == 0)
            {
                return result;
            }

            bool IsSlotAnimated(int slot)
            {
                if (slot < 0 || slot >= indexArray.Length)
                {
                    return false;
                }
                int curveIdx = indexArray[slot];
                if (curveIdx < 0)
                {
                    return false;
                }
                if (curveIdx < streamCount)
                {
                    return streamedEval != null && streamedEval.HasCurve(curveIdx);
                }
                if (curveIdx < streamCount + denseCount)
                {
                    return hasDense;
                }
                int constIdx = (int)(curveIdx - (streamCount + denseCount));
                return constantClip?.data != null && constIdx >= 0 && constIdx < constantClip.data.Length;
            }

            float EvalSlot(int slot, float time)
            {
                if (slot < 0 || slot >= indexArray.Length)
                {
                    return 0f;
                }
                int curveIdx = indexArray[slot];
                if (curveIdx < 0)
                {
                    return 0f;
                }

                if (curveIdx < streamCount)
                {
                    return streamedEval != null ? streamedEval.EvaluateOrDefault(curveIdx, time, 0f) : 0f;
                }

                if (curveIdx < streamCount + denseCount)
                {
                    if (!hasDense)
                    {
                        return 0f;
                    }
                    int denseIdx = (int)(curveIdx - streamCount);
                    float fPos = (time - denseClip!.m_BeginTime) * denseSampleRate;
                    if (!float.IsFinite(fPos))
                    {
                        fPos = 0f;
                    }
                    fPos = Math.Clamp(fPos, 0f, Math.Max(0, denseClip.m_FrameCount - 1));
                    int f0 = (int)MathF.Floor(fPos);
                    int f1 = Math.Min(f0 + 1, denseClip.m_FrameCount - 1);
                    float frac = fPos - f0;
                    int off0 = f0 * (int)denseCount + denseIdx;
                    int off1 = f1 * (int)denseCount + denseIdx;
                    var samples = denseClip.m_SampleArray;
                    if (samples == null || (uint)off0 >= (uint)samples.Length)
                    {
                        return 0f;
                    }
                    float v0 = samples[off0];
                    if (f0 == f1 || (uint)off1 >= (uint)samples.Length)
                    {
                        return float.IsFinite(v0) ? v0 : 0f;
                    }
                    float v1 = samples[off1];
                    float interp = v0 + (v1 - v0) * frac;
                    return float.IsFinite(interp) ? interp : (float.IsFinite(v0) ? v0 : 0f);
                }

                int constIdx = (int)(curveIdx - (streamCount + denseCount));
                if (constantClip?.data != null && (uint)constIdx < (uint)constantClip.data.Length)
                {
                    float v = constantClip.data[constIdx];
                    return float.IsFinite(v) ? v : 0f;
                }
                return 0f;
            }

            // Collect active bone bindings (body + 30 fingers)
            var activeBones = new List<(int HSkelIdx, int AvNodeIdx, Axes Axes, Quaternion IntermediateInvQ, int[] MuscleSlots, int[] DofAxes)>();
            var visitedAvNodes = new HashSet<int>();

            int pelvisHSkel = human.m_HumanBoneIndex[0];
            int pelvisAvNode = (pelvisHSkel >= 0 && pelvisHSkel < hIdxArr.Length) ? hIdxArr[pelvisHSkel] : -1;
            if (pelvisAvNode >= 0)
            {
                visitedAvNodes.Add(pelvisAvNode);
            }

            var avPose = avConst.m_AvatarSkeletonPose?.m_X ?? avConst.m_DefaultPose?.m_X;
            var defPose = avConst.m_DefaultPose?.m_X ?? avConst.m_AvatarSkeletonPose?.m_X;

            void TryAddBone(int hSkelIdx, int muscleStart56, int[] dofAxes)
            {
                if (hSkelIdx < 0 || hSkelIdx >= hSkel.m_Node.Length || hSkelIdx >= hIdxArr.Length)
                {
                    return;
                }
                int avNode = hIdxArr[hSkelIdx];
                if (avNode < 0 || avNode >= avSkel.m_Node.Length || avNode >= avSkel.m_ID.Length)
                {
                    return;
                }
                if (!visitedAvNodes.Add(avNode))
                {
                    return;
                }

                var hNode = hSkel.m_Node[hSkelIdx];
                if (hNode.m_AxesId < 0 || hNode.m_AxesId >= hSkel.m_AxesArray.Length)
                {
                    return;
                }
                var axes = hSkel.m_AxesArray[hNode.m_AxesId];
                if (axes == null)
                {
                    return;
                }

                // Shift muscle index down by 3 for legacy < 5.6 92-muscle clips (which lacked UpperChest m6..m8)
                int effectiveStart = isLegacyClip92 && muscleStart56 >= 9 ? muscleStart56 - 3 : muscleStart56;
                var slots = new int[dofAxes.Length];
                bool anyAnimated = false;
                for (int i = 0; i < dofAxes.Length; i++)
                {
                    int slot = MuscleSlotOffset + effectiveStart + i;
                    slots[i] = slot;
                    if (IsSlotAnimated(slot))
                    {
                        anyAnimated = true;
                    }
                }
                if (!anyAnimated)
                {
                    return;
                }

                // Compensate for any intermediate unmapped bones between this bone's avNode and its Human parent's avNode
                var interInvQ = ComputeIntermediateParentInverseQ(avSkel, avPose, hSkel, hIdxArr, hSkelIdx, avNode);
                activeBones.Add((hSkelIdx, avNode, axes, interInvQ, slots, dofAxes));
            }

            // Body bones
            foreach (var def in BodyBoneDefs)
            {
                if (isLegacyClip92 && def.BodyIndexLegacy < 0)
                {
                    continue; // UpperChest did not exist in legacy 92-muscle clips
                }
                int bodyIdx = isLegacyAvatar24 ? def.BodyIndexLegacy : def.BodyIndex56;
                if (bodyIdx < 0 || bodyIdx >= human.m_HumanBoneIndex.Length)
                {
                    continue;
                }
                TryAddBone(human.m_HumanBoneIndex[bodyIdx], def.MuscleStart56, def.DofAxes);
            }

            // Left hand (15 bones -> 20 muscles at m55..m74 in 5.6+)
            if (human.m_LeftHand?.m_HandBoneIndex != null)
            {
                AddHandBones(human.m_LeftHand.m_HandBoneIndex, 55, TryAddBone);
            }

            // Right hand (15 bones -> 20 muscles at m75..m94 in 5.6+)
            if (human.m_RightHand?.m_HandBoneIndex != null)
            {
                AddHandBones(human.m_RightHand.m_HandBoneIndex, 75, TryAddBone);
            }

            // Check RootT / RootQ channels at m_IndexArray[7..13]
            bool hasRootT = IsSlotAnimated(RootSlotOffset) || IsSlotAnimated(RootSlotOffset + 1) || IsSlotAnimated(RootSlotOffset + 2);
            bool hasRootQ = IsSlotAnimated(RootSlotOffset + 3) || IsSlotAnimated(RootSlotOffset + 4) || IsSlotAnimated(RootSlotOffset + 5) || IsSlotAnimated(RootSlotOffset + 6);

            if (!hasRootT && !hasRootQ && activeBones.Count == 0)
            {
                return result;
            }

            // Pre-allocate bone tracks
            var boneTracks = new DecodedBoneTrack[activeBones.Count];
            for (int b = 0; b < activeBones.Count; b++)
            {
                int avNode = activeBones[b].AvNodeIdx;
                string path = avatar.FindBonePath(avSkel.m_ID[avNode]) ?? $"Bone_{avSkel.m_ID[avNode]}";
                boneTracks[b] = new DecodedBoneTrack
                {
                    AvatarNodeIndex = avNode,
                    BonePath = path
                };
            }

            // Prepare Pelvis track and Avatar-space <-> Parent-local-space transform
            DecodedBoneTrack? pelvisTrack = null;
            var hPose = human.m_SkeletonPose?.m_X;
            bool canComputePelvis = pelvisAvNode >= 0 && pelvisAvNode < avSkel.m_ID.Length && (hasRootT || hasRootQ);
            float humanScale = human.m_Scale > 1e-5f && float.IsFinite(human.m_Scale) ? human.m_Scale : 1f;
            Vector3 pelvisHumanT = (hPose != null && pelvisHSkel >= 0 && pelvisHSkel < hPose.Length) ? hPose[pelvisHSkel].t : Vector3.Zero;
            Quaternion pelvisHumanQ = (hPose != null && pelvisHSkel >= 0 && pelvisHSkel < hPose.Length)
                ? NormalizeQuat(hPose[pelvisHSkel].q)
                : new Quaternion(0f, 0f, 0f, 1f);
            Vector3 rootXt = human.m_RootX?.t ?? Vector3.Zero;
            Vector3 staticPelvisOffsetFromCom = pelvisHumanT - rootXt;

            Vector3 parentAvT = Vector3.Zero;
            Quaternion parentAvQ = new Quaternion(0f, 0f, 0f, 1f);
            Vector3 parentAvS = Vector3.One;
            Quaternion parentAvQInv = new Quaternion(0f, 0f, 0f, 1f);

            if (canComputePelvis)
            {
                string pelvisPath = avatar.FindBonePath(avSkel.m_ID[pelvisAvNode]) ?? $"Bone_{avSkel.m_ID[pelvisAvNode]}";
                pelvisTrack = new DecodedBoneTrack
                {
                    AvatarNodeIndex = pelvisAvNode,
                    BonePath = pelvisPath
                };
                ComputeAncestorAvatarTransform(avSkel, defPose, pelvisAvNode, out parentAvT, out parentAvQ, out parentAvS);
                parentAvQInv = ConjugateQuat(parentAvQ);
            }

            // Precompute whether we can compute dynamic pose Center of Mass via m_HumanBoneMass
            bool canComputeMassCom = canComputePelvis
                && hasRootT
                && hPose != null && hPose.Length >= hSkel.m_Node.Length
                && human.m_HumanBoneMass != null && human.m_HumanBoneMass.Length > 0;
            float totalMass = 0f;
            if (canComputeMassCom)
            {
                int massLen = Math.Min(human.m_HumanBoneMass!.Length, human.m_HumanBoneIndex.Length);
                for (int i = 0; i < massLen; i++)
                {
                    int hs = human.m_HumanBoneIndex[i];
                    if (hs >= 0 && hs < hSkel.m_Node.Length && human.m_HumanBoneMass[i] > 0f)
                    {
                        totalMass += human.m_HumanBoneMass[i];
                    }
                }
                if (totalMass <= 1e-5f)
                {
                    canComputeMassCom = false;
                }
            }

            Quaternion[]? frameHumanLocalQ = canComputeMassCom ? new Quaternion[hSkel.m_Node.Length] : null;
            Vector3[]? frameRelT = canComputeMassCom ? new Vector3[hSkel.m_Node.Length] : null;
            Quaternion[]? frameRelQ = canComputeMassCom ? new Quaternion[hSkel.m_Node.Length] : null;

            for (int f = 0; f < sampleTimes.Length; f++)
            {
                float time = sampleTimes[f];

                if (frameHumanLocalQ != null && hPose != null)
                {
                    for (int i = 0; i < frameHumanLocalQ.Length; i++)
                    {
                        frameHumanLocalQ[i] = NormalizeQuat(hPose[i].q);
                    }
                }

                // 1. Evaluate all animated muscle bones
                for (int b = 0; b < activeBones.Count; b++)
                {
                    var bone = activeBones[b];
                    var axes = bone.Axes;
                    var preQ = new Quaternion(axes.m_PreQ.X, axes.m_PreQ.Y, axes.m_PreQ.Z, axes.m_PreQ.W);
                    var postQ = new Quaternion(axes.m_PostQ.X, axes.m_PostQ.Y, axes.m_PostQ.Z, axes.m_PostQ.W);
                    var min = ToVec3(axes.m_Limit?.m_Min);
                    var max = ToVec3(axes.m_Limit?.m_Max);
                    var sgn = ToVec3(axes.m_Sgn);

                    float thetaX = 0f, thetaY = 0f, thetaZ = 0f;
                    for (int k = 0; k < bone.DofAxes.Length; k++)
                    {
                        float val = EvalSlot(bone.MuscleSlots[k], time);
                        int axis = bone.DofAxes[k];
                        float minVal = axis == 0 ? min.X : (axis == 1 ? min.Y : min.Z);
                        float maxVal = axis == 0 ? max.X : (axis == 1 ? max.Y : max.Z);
                        float sgnVal = axis == 0 ? sgn.X : (axis == 1 ? sgn.Y : sgn.Z);
                        float angle = (val >= 0f ? val * maxVal : -val * minVal) * sgnVal;
                        if (axis == 0) thetaX = angle;
                        else if (axis == 1) thetaY = angle;
                        else thetaZ = angle;
                    }

                    var qHumanLocal = ComputeMecanimMuscleLocalRotation(preQ, postQ, thetaX, thetaY, thetaZ);
                    if (frameHumanLocalQ != null && bone.HSkelIdx < frameHumanLocalQ.Length)
                    {
                        frameHumanLocalQ[bone.HSkelIdx] = qHumanLocal;
                    }

                    var qAvLocal = NormalizeQuat(MultiplyQuat(bone.IntermediateInvQ, qHumanLocal));
                    boneTracks[b].Rotations.Add(new ImportedKeyframe<Quaternion>(time, qAvLocal));
                }

                // 2. Evaluate Pelvis RootT / RootQ in Avatar space -> transform to Pelvis parent-local space
                if (pelvisTrack != null)
                {
                    var rootQ = hasRootQ
                        ? NormalizeQuat(new Quaternion(
                            EvalSlot(RootSlotOffset + 3, time),
                            EvalSlot(RootSlotOffset + 4, time),
                            EvalSlot(RootSlotOffset + 5, time),
                            EvalSlot(RootSlotOffset + 6, time)))
                        : new Quaternion(0f, 0f, 0f, 1f);

                    var qPelvisAvatar = NormalizeQuat(MultiplyQuat(rootQ, pelvisHumanQ));
                    var qPelvisLocal = NormalizeQuat(MultiplyQuat(parentAvQInv, qPelvisAvatar));

                    if (hasRootQ)
                    {
                        pelvisTrack.Rotations.Add(new ImportedKeyframe<Quaternion>(time, qPelvisLocal));
                    }

                    if (hasRootT)
                    {
                        var rootT = new Vector3(
                            EvalSlot(RootSlotOffset, time) * humanScale,
                            EvalSlot(RootSlotOffset + 1, time) * humanScale,
                            EvalSlot(RootSlotOffset + 2, time) * humanScale);

                        Vector3 pelvisAvatarT;
                        if (canComputeMassCom && human.m_HumanBoneMass != null && frameHumanLocalQ != null && frameRelT != null && frameRelQ != null && hPose != null)
                        {
                            // Compute instantaneous pose Center of Mass relative to Pelvis using m_HumanBoneMass
                            frameRelT[0] = Vector3.Zero;
                            frameRelQ[0] = new Quaternion(0f, 0f, 0f, 1f);
                            for (int i = 1; i < hSkel.m_Node.Length; i++)
                            {
                                if (i == pelvisHSkel)
                                {
                                    frameRelT[i] = Vector3.Zero;
                                    frameRelQ[i] = pelvisHumanQ;
                                }
                                else
                                {
                                    int p = hSkel.m_Node[i].m_ParentId;
                                    if (p < 0 || p >= i) p = 0;
                                    frameRelT[i] = frameRelT[p] + RotateVec(frameRelQ[p], hPose[i].t);
                                    frameRelQ[i] = NormalizeQuat(MultiplyQuat(frameRelQ[p], frameHumanLocalQ[i]));
                                }
                            }

                            var comRel = Vector3.Zero;
                            int massLen = Math.Min(human.m_HumanBoneMass!.Length, human.m_HumanBoneIndex.Length);
                            for (int i = 0; i < massLen; i++)
                            {
                                float m = human.m_HumanBoneMass[i];
                                int hs = human.m_HumanBoneIndex[i];
                                if (m <= 0f || hs < 0 || hs >= hSkel.m_Node.Length)
                                {
                                    continue;
                                }
                                var pPos = frameRelT[hs];
                                int axId = hSkel.m_Node[hs].m_AxesId;
                                if (axId >= 0 && axId < hSkel.m_AxesArray.Length)
                                {
                                    var ax = hSkel.m_AxesArray[axId];
                                    var postQ = NormalizeQuat(new Quaternion(ax.m_PostQ.X, ax.m_PostQ.Y, ax.m_PostQ.Z, ax.m_PostQ.W));
                                    var canQ = NormalizeQuat(MultiplyQuat(frameRelQ[hs], postQ));
                                    pPos += RotateVec(canQ, new Vector3(ax.m_Length * 0.5f, 0f, 0f));
                                }
                                comRel += pPos * m;
                            }
                            comRel = new Vector3(comRel.X / totalMass, comRel.Y / totalMass, comRel.Z / totalMass);
                            pelvisAvatarT = rootT - RotateVec(rootQ, comRel);
                        }
                        else
                        {
                            pelvisAvatarT = rootT + RotateVec(rootQ, staticPelvisOffsetFromCom);
                        }

                        var deltaInParent = RotateVec(parentAvQInv, pelvisAvatarT - parentAvT);
                        var tPelvisLocal = new Vector3(
                            MathF.Abs(parentAvS.X) > 1e-6f ? deltaInParent.X / parentAvS.X : deltaInParent.X,
                            MathF.Abs(parentAvS.Y) > 1e-6f ? deltaInParent.Y / parentAvS.Y : deltaInParent.Y,
                            MathF.Abs(parentAvS.Z) > 1e-6f ? deltaInParent.Z / parentAvS.Z : deltaInParent.Z);

                        pelvisTrack.Translations.Add(new ImportedKeyframe<Vector3>(time, tPelvisLocal));
                    }
                }
            }

            if (pelvisTrack != null && (pelvisTrack.Translations.Count > 0 || pelvisTrack.Rotations.Count > 0))
            {
                result.Add(pelvisTrack);
            }
            for (int b = 0; b < boneTracks.Length; b++)
            {
                if (boneTracks[b].Rotations.Count > 0)
                {
                    result.Add(boneTracks[b]);
                }
            }
            return result;
        }

        private static void AddHandBones(int[] handBoneIndex, int handMuscleBase56, Action<int, int, int[]> addBone)
        {
            int count = Math.Min(15, handBoneIndex.Length);
            for (int i = 0; i < count; i++)
            {
                int hSkelIdx = handBoneIndex[i];
                if (hSkelIdx < 0)
                {
                    continue;
                }

                int finger = i / 3;  // 0=Thumb, 1=Index, 2=Middle, 3=Ring, 4=Little
                int segment = i % 3; // 0=Proximal (2 DoF), 1=Intermediate (1 DoF), 2=Distal (1 DoF)
                int mStart = handMuscleBase56 + finger * 4 + (segment == 0 ? 0 : segment + 1);
                var dofAxes = segment == 0 ? FingerProximalDofAxes : FingerHingeDofAxes;
                addBone(hSkelIdx, mStart, dofAxes);
            }
        }

        private static bool IsLegacy92MuscleClip(AnimationClip clip, int indexArrayLength)
        {
            if (clip.version != null && clip.version.Length >= 2 && clip.version[0] > 0)
            {
                return clip.version[0] < 5 || (clip.version[0] == 5 && clip.version[1] < 6);
            }
            // Fallback for synthetic clips where version isn't initialized:
            // Unity 5.6+ has 95 muscles (42+95 = 137 minimum, typically 200 with TDoF); legacy 5.3 has 155 (42+92+21).
            return indexArrayLength < MuscleSlotOffset + ModernMuscleCount || indexArrayLength == 155;
        }

        private static float[] BuildSampleTimestamps(
            ClipMuscleConstant muscleClip,
            DenseClip? denseClip,
            bool hasDense,
            float denseSampleRate,
            float clipSampleRate,
            StreamedCurveEvaluator? streamedEval)
        {
            if (hasDense && denseClip != null)
            {
                int count = denseClip.m_FrameCount;
                var times = new float[count];
                float begin = float.IsFinite(denseClip.m_BeginTime) ? denseClip.m_BeginTime : 0f;
                for (int f = 0; f < count; f++)
                {
                    float t = begin + f / denseSampleRate;
                    times[f] = t < 0f ? 0f : t;
                }
                return times;
            }

            float start = float.IsFinite(muscleClip.m_StartTime) && muscleClip.m_StartTime > 0f ? muscleClip.m_StartTime : 0f;
            float stop = float.IsFinite(muscleClip.m_StopTime) && muscleClip.m_StopTime > start ? muscleClip.m_StopTime : start;

            if (streamedEval != null && streamedEval.InteriorTimes.Count > 0)
            {
                float sMin = streamedEval.InteriorTimes[0];
                float sMax = streamedEval.InteriorTimes[^1];
                if (stop <= start)
                {
                    start = sMin;
                    stop = sMax;
                }
                else
                {
                    start = Math.Min(start, sMin);
                    stop = Math.Max(stop, sMax);
                }

                float span = stop - start;
                if (span <= 1e-6f)
                {
                    return new[] { start };
                }

                // Sample uniformly at clipSampleRate (clamped for safety) so cubic Hermite curves
                // are faithfully reproduced across all bones, and include exact streamed key times.
                float rate = Math.Clamp(clipSampleRate, 1f, 120f);
                int steps = Math.Clamp((int)MathF.Ceiling(span * rate), 1, 60000);
                var merged = new List<float>(steps + 1 + streamedEval.InteriorTimes.Count);
                for (int i = 0; i <= steps; i++)
                {
                    float t = i == steps ? stop : start + i / rate;
                    if (t > stop) t = stop;
                    merged.Add(t);
                }
                for (int i = 0; i < streamedEval.InteriorTimes.Count; i++)
                {
                    float st = streamedEval.InteriorTimes[i];
                    if (st >= start - 1e-5f && st <= stop + 1e-5f)
                    {
                        merged.Add(Math.Clamp(st, start, stop));
                    }
                }
                merged.Sort();

                var dedup = new List<float>(merged.Count);
                for (int i = 0; i < merged.Count; i++)
                {
                    if (dedup.Count == 0 || merged[i] - dedup[^1] > 1e-5f)
                    {
                        dedup.Add(merged[i]);
                    }
                }
                return dedup.ToArray();
            }

            // Constant-only clip: avoid duplicate t=0 key when stop == start
            if (stop > start + 1e-6f)
            {
                return new[] { start, stop };
            }
            return new[] { start };
        }

        private static Quaternion ComputeIntermediateParentInverseQ(
            Skeleton avSkel,
            XForm[]? avPose,
            Skeleton hSkel,
            int[] hIdxArr,
            int hSkelIdx,
            int avNode)
        {
            if (avPose == null || avSkel.m_Node == null)
            {
                return new Quaternion(0f, 0f, 0f, 1f);
            }

            int hParent = hSkel.m_Node[hSkelIdx].m_ParentId;
            if (hParent < 0 || hParent >= hIdxArr.Length)
            {
                return new Quaternion(0f, 0f, 0f, 1f);
            }
            int humanParentAvNode = hIdxArr[hParent];
            int directParentAvNode = avSkel.m_Node[avNode].m_ParentId;
            if (directParentAvNode == humanParentAvNode || directParentAvNode < 0)
            {
                return new Quaternion(0f, 0f, 0f, 1f);
            }

            var chain = new List<int>();
            int cur = directParentAvNode;
            int guard = 0;
            while (cur >= 0 && cur < avSkel.m_Node.Length && cur != humanParentAvNode && guard++ < 64)
            {
                chain.Add(cur);
                cur = avSkel.m_Node[cur].m_ParentId;
            }
            if (cur != humanParentAvNode || chain.Count == 0)
            {
                return new Quaternion(0f, 0f, 0f, 1f);
            }

            var qInter = new Quaternion(0f, 0f, 0f, 1f);
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                int idx = chain[i];
                if (idx >= 0 && idx < avPose.Length)
                {
                    qInter = NormalizeQuat(MultiplyQuat(qInter, NormalizeQuat(avPose[idx].q)));
                }
            }
            return ConjugateQuat(qInter);
        }

        private static void ComputeAncestorAvatarTransform(
            Skeleton avSkel,
            XForm[]? pose,
            int targetNode,
            out Vector3 parentT,
            out Quaternion parentQ,
            out Vector3 parentS)
        {
            parentT = Vector3.Zero;
            parentQ = new Quaternion(0f, 0f, 0f, 1f);
            parentS = Vector3.One;

            if (pose == null || avSkel.m_Node == null || targetNode < 0 || targetNode >= avSkel.m_Node.Length)
            {
                return;
            }

            var chain = new List<int>();
            int cur = avSkel.m_Node[targetNode].m_ParentId;
            int guard = 0;
            while (cur >= 0 && cur < avSkel.m_Node.Length && guard++ < 128)
            {
                chain.Add(cur);
                cur = avSkel.m_Node[cur].m_ParentId;
            }

            for (int i = chain.Count - 1; i >= 0; i--)
            {
                int idx = chain[i];
                if (idx < 0 || idx >= pose.Length)
                {
                    continue;
                }
                var xf = pose[idx];
                var scaledT = new Vector3(xf.t.X * parentS.X, xf.t.Y * parentS.Y, xf.t.Z * parentS.Z);
                parentT += RotateVec(parentQ, scaledT);
                parentQ = NormalizeQuat(MultiplyQuat(parentQ, NormalizeQuat(xf.q)));
                parentS = new Vector3(parentS.X * xf.s.X, parentS.Y * xf.s.Y, parentS.Z * xf.s.Z);
            }
        }

        public static Quaternion MultiplyQuat(Quaternion p, Quaternion q)
        {
            return new Quaternion(
                p.W * q.X + p.X * q.W + p.Y * q.Z - p.Z * q.Y,
                p.W * q.Y - p.X * q.Z + p.Y * q.W + p.Z * q.X,
                p.W * q.Z + p.X * q.Y - p.Y * q.X + p.Z * q.W,
                p.W * q.W - p.X * q.X - p.Y * q.Y - p.Z * q.Z);
        }

        public static Quaternion ConjugateQuat(Quaternion q)
        {
            return new Quaternion(-q.X, -q.Y, -q.Z, q.W);
        }

        public static Quaternion NormalizeQuat(Quaternion q)
        {
            float n2 = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
            if (!float.IsFinite(n2) || n2 <= 1e-12f)
            {
                return new Quaternion(0f, 0f, 0f, 1f);
            }
            float inv = 1f / MathF.Sqrt(n2);
            return new Quaternion(q.X * inv, q.Y * inv, q.Z * inv, q.W * inv);
        }

        public static Vector3 RotateVec(Quaternion q, Vector3 v)
        {
            float tx = 2f * (q.Y * v.Z - q.Z * v.Y);
            float ty = 2f * (q.Z * v.X - q.X * v.Z);
            float tz = 2f * (q.X * v.Y - q.Y * v.X);
            return new Vector3(
                v.X + q.W * tx + (q.Y * tz - q.Z * ty),
                v.Y + q.W * ty + (q.Z * tx - q.X * tz),
                v.Z + q.W * tz + (q.X * ty - q.Y * tx));
        }

        public static Vector3 ToVec3(object? obj)
        {
            if (obj is Vector3 v3) return v3;
            if (obj is Vector4 v4) return new Vector3(v4.X, v4.Y, v4.Z);
            return Vector3.Zero;
        }

        private static bool IsFiniteVec(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }
}
