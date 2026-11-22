using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AssetStudio
{
    public class Keyframe<T> where T : struct
    {
        public float time;
        public T value;
        public T inSlope;
        public T outSlope;
        public int weightedMode;
        public T inWeight;
        public T outWeight;


        public Keyframe(ObjectReader reader, Func<T> readerFunc)
        {
            time = reader.ReadSingle();
            value = readerFunc();
            inSlope = readerFunc();
            outSlope = readerFunc();
            if (reader.version[0] >= 2018) //2018 and up
            {
                weightedMode = reader.ReadInt32();
                inWeight = readerFunc();
                outWeight = readerFunc();
            }
        }
    }

    public class AnimationCurve<T> where T : struct
    {
        public Keyframe<T>[] m_Curve;
        public int m_PreInfinity;
        public int m_PostInfinity;
        public int m_RotationOrder;

        public AnimationCurve(ObjectReader reader, Func<T> readerFunc)
        {
            var version = reader.version;
            int numCurves = reader.ReadInt32();
            reader.CheckArrayLength(numCurves);
            m_Curve = new Keyframe<T>[numCurves];
            for (int i = 0; i < numCurves; i++)
            {
                m_Curve[i] = new Keyframe<T>(reader, readerFunc);
            }

            m_PreInfinity = reader.ReadInt32();
            m_PostInfinity = reader.ReadInt32();
            if (version[0] > 5 || (version[0] == 5 && version[1] >= 3))//5.3 and up
            {
                m_RotationOrder = reader.ReadInt32();
            }
        }
    }

    public class QuaternionCurve
    {
        public AnimationCurve<Quaternion> curve;
        public string path;

        public QuaternionCurve(ObjectReader reader)
        {
            curve = new AnimationCurve<Quaternion>(reader, reader.ReadQuaternion);
            path = reader.ReadAlignedString();
        }
    }

    internal static class PackedVectorHelper
    {
        public const int MaxUnpackItemCount = 16_777_216;

        public static uint ReadBits(byte[] data, int bitSize, ref int indexPos, ref int bitPos)
        {
            uint value = 0;
            int bits = 0;
            while (bits < bitSize)
            {
                if ((uint)indexPos >= (uint)data.Length)
                {
                    throw new InvalidDataException($"Packed bit stream exceeded data buffer length ({data.Length} bytes).");
                }
                value |= ((uint)(data[indexPos] >> bitPos)) << bits;
                int num = Math.Min(bitSize - bits, 8 - bitPos);
                bitPos += num;
                bits += num;
                if (bitPos == 8)
                {
                    indexPos++;
                    bitPos = 0;
                }
            }

            uint mask = bitSize == 32 ? uint.MaxValue : (1u << bitSize) - 1u;
            return value & mask;
        }
    }

    public class PackedFloatVector
    {
        public uint m_NumItems;
        public float m_Range;
        public float m_Start;
        public byte[] m_Data = Array.Empty<byte>();
        public byte m_BitSize;

        public PackedFloatVector() { }

        public PackedFloatVector(ObjectReader reader)
        {
            m_NumItems = reader.ReadUInt32();
            m_Range = reader.ReadSingle();
            m_Start = reader.ReadSingle();

            int numData = reader.ReadInt32();
            reader.CheckArrayLength(numData);
            m_Data = reader.ReadBytes(numData);
            reader.AlignStream();

            m_BitSize = reader.ReadByte();
            reader.AlignStream();
        }

        public float[] UnpackFloats(int itemCountInChunk, int chunkStride, int start = 0, int numChunks = -1)
        {
            if (itemCountInChunk <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(itemCountInChunk), itemCountInChunk, "itemCountInChunk must be positive.");
            }
            if (chunkStride < 4 || (chunkStride % 4) != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkStride), chunkStride, "chunkStride must be a positive multiple of 4 bytes.");
            }
            if (start < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(start), start, "start must be non-negative.");
            }
            if (numChunks < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(numChunks), numChunks, "numChunks must be -1 or non-negative.");
            }
            if (m_BitSize > 32)
            {
                throw new InvalidDataException($"Invalid PackedFloatVector bit size: {m_BitSize}. Maximum supported is 32.");
            }

            long resolvedNumChunks;
            if (numChunks == -1)
            {
                if (m_NumItems == 0)
                {
                    if (start > 0)
                    {
                        throw new InvalidDataException($"Start item index {start} exceeds PackedFloatVector item count {m_NumItems}.");
                    }
                    return Array.Empty<float>();
                }
                if ((long)start > m_NumItems)
                {
                    throw new InvalidDataException($"Start item index {start} exceeds PackedFloatVector item count {m_NumItems}.");
                }
                resolvedNumChunks = ((long)m_NumItems - start) / itemCountInChunk;
            }
            else
            {
                resolvedNumChunks = numChunks;
            }

            long totalItems = resolvedNumChunks * itemCountInChunk;
            if (totalItems > PackedVectorHelper.MaxUnpackItemCount)
            {
                throw new InvalidDataException($"PackedFloatVector unpack count {totalItems} exceeds maximum allowed limit ({PackedVectorHelper.MaxUnpackItemCount}).");
            }
            if ((long)start + totalItems > m_NumItems)
            {
                throw new InvalidDataException($"Requested unpack range [{start}, {(long)start + totalItems}) exceeds PackedFloatVector item count {m_NumItems}.");
            }
            if (totalItems == 0)
            {
                return Array.Empty<float>();
            }
            if (m_Data == null)
            {
                throw new InvalidDataException("PackedFloatVector data buffer is null.");
            }

            int outputCount = (int)totalItems;
            if (m_BitSize == 0)
            {
                var constantData = new float[outputCount];
                Array.Fill(constantData, m_Start);
                return constantData;
            }

            long startBitPos = (long)m_BitSize * start;
            long totalBits = totalItems * m_BitSize;
            long requiredBytes = (startBitPos + totalBits + 7) / 8;
            if (requiredBytes > m_Data.Length)
            {
                throw new InvalidDataException($"PackedFloatVector data length ({m_Data.Length} bytes) is smaller than required ({requiredBytes} bytes for {outputCount} items at {m_BitSize} bits starting at item {start}).");
            }

            int indexPos = (int)(startBitPos / 8);
            int bitPos = (int)(startBitPos % 8);

            uint maxValue = m_BitSize == 32 ? uint.MaxValue : (1u << m_BitSize) - 1u;
            float scale = 1.0f / m_Range;
            float divisor = scale * maxValue;

            var data = new float[outputCount];
            for (int outIndex = 0; outIndex < outputCount; outIndex++)
            {
                uint x = PackedVectorHelper.ReadBits(m_Data, m_BitSize, ref indexPos, ref bitPos);
                data[outIndex] = x / divisor + m_Start;
            }

            return data;
        }
    }

    public class PackedIntVector
    {
        public uint m_NumItems;
        public byte[] m_Data = Array.Empty<byte>();
        public byte m_BitSize;

        public PackedIntVector() { }

        public PackedIntVector(ObjectReader reader)
        {
            m_NumItems = reader.ReadUInt32();

            int numData = reader.ReadInt32();
            reader.CheckArrayLength(numData);
            m_Data = reader.ReadBytes(numData);
            reader.AlignStream();

            m_BitSize = reader.ReadByte();
            reader.AlignStream();
        }

        public int[] UnpackInts()
        {
            if (m_BitSize > 32)
            {
                throw new InvalidDataException($"Invalid PackedIntVector bit size: {m_BitSize}. Maximum supported is 32.");
            }
            if (m_NumItems == 0)
            {
                return Array.Empty<int>();
            }
            if (m_NumItems > PackedVectorHelper.MaxUnpackItemCount)
            {
                throw new InvalidDataException($"PackedIntVector item count {m_NumItems} exceeds maximum allowed limit ({PackedVectorHelper.MaxUnpackItemCount}).");
            }
            if (m_Data == null)
            {
                throw new InvalidDataException("PackedIntVector data buffer is null.");
            }

            int count = (int)m_NumItems;
            if (m_BitSize == 0)
            {
                return new int[count];
            }

            long totalBits = (long)count * m_BitSize;
            long requiredBytes = (totalBits + 7) / 8;
            if (requiredBytes > m_Data.Length)
            {
                throw new InvalidDataException($"PackedIntVector data length ({m_Data.Length} bytes) is smaller than required ({requiredBytes} bytes for {count} items at {m_BitSize} bits).");
            }

            var data = new int[count];
            int indexPos = 0;
            int bitPos = 0;
            for (int i = 0; i < count; i++)
            {
                data[i] = (int)PackedVectorHelper.ReadBits(m_Data, m_BitSize, ref indexPos, ref bitPos);
            }
            return data;
        }
    }

    public class PackedQuatVector
    {
        public uint m_NumItems;
        public byte[] m_Data = Array.Empty<byte>();

        public PackedQuatVector() { }

        public PackedQuatVector(ObjectReader reader)
        {
            m_NumItems = reader.ReadUInt32();

            int numData = reader.ReadInt32();
            reader.CheckArrayLength(numData);
            m_Data = reader.ReadBytes(numData);

            reader.AlignStream();
        }

        public Quaternion[] UnpackQuats()
        {
            if (m_NumItems == 0)
            {
                return Array.Empty<Quaternion>();
            }
            if (m_NumItems > PackedVectorHelper.MaxUnpackItemCount)
            {
                throw new InvalidDataException($"PackedQuatVector item count {m_NumItems} exceeds maximum allowed limit ({PackedVectorHelper.MaxUnpackItemCount}).");
            }
            if (m_Data == null)
            {
                throw new InvalidDataException("PackedQuatVector data buffer is null.");
            }

            int count = (int)m_NumItems;
            // Each packed quaternion uses 3 flag bits + 9 bits + 10 bits + 10 bits = 32 bits (4 bytes).
            long requiredBytes = (long)count * 4;
            if (requiredBytes > m_Data.Length)
            {
                throw new InvalidDataException($"PackedQuatVector data length ({m_Data.Length} bytes) is smaller than required ({requiredBytes} bytes for {count} quaternions).");
            }

            var data = new Quaternion[count];
            int indexPos = 0;
            int bitPos = 0;

            for (int i = 0; i < count; i++)
            {
                uint flags = PackedVectorHelper.ReadBits(m_Data, 3, ref indexPos, ref bitPos);

                var q = new Quaternion();
                float sum = 0;
                for (int j = 0; j < 4; j++)
                {
                    if ((flags & 3) != j)
                    {
                        int bitSize = ((flags & 3) + 1) % 4 == j ? 9 : 10;
                        uint x = PackedVectorHelper.ReadBits(m_Data, bitSize, ref indexPos, ref bitPos);
                        q[j] = x / (0.5f * ((1 << bitSize) - 1)) - 1;
                        sum += q[j] * q[j];
                    }
                }

                int lastComponent = (int)(flags & 3);
                float remaining = 1.0f - sum;
                q[lastComponent] = remaining > 0f ? (float)Math.Sqrt(remaining) : 0f;
                if ((flags & 4) != 0u)
                    q[lastComponent] = -q[lastComponent];
                data[i] = q;
            }

            return data;
        }
    }

    public class CompressedAnimationCurve
    {
        public string m_Path;
        public PackedIntVector m_Times;
        public PackedQuatVector m_Values;
        public PackedFloatVector m_Slopes;
        public int m_PreInfinity;
        public int m_PostInfinity;

        public CompressedAnimationCurve(ObjectReader reader)
        {
            m_Path = reader.ReadAlignedString();
            m_Times = new PackedIntVector(reader);
            m_Values = new PackedQuatVector(reader);
            m_Slopes = new PackedFloatVector(reader);
            m_PreInfinity = reader.ReadInt32();
            m_PostInfinity = reader.ReadInt32();
        }
    }

    public class Vector3Curve
    {
        public AnimationCurve<Vector3> curve;
        public string path;

        public Vector3Curve(ObjectReader reader)
        {
            curve = new AnimationCurve<Vector3>(reader, reader.ReadVector3);
            path = reader.ReadAlignedString();
        }
    }

    public class FloatCurve
    {
        public AnimationCurve<float> curve;
        public string attribute;
        public string path;
        public ClassIDType classID;
        public PPtr<MonoScript> script;
        public int flags;


        public FloatCurve(ObjectReader reader)
        {
            var version = reader.version;
            curve = new AnimationCurve<float>(reader, reader.ReadSingle);
            attribute = reader.ReadAlignedString();
            path = reader.ReadAlignedString();
            classID = (ClassIDType)reader.ReadInt32();
            script = new PPtr<MonoScript>(reader);
            if (version[0] > 2022 || (version[0] == 2022 && version[1] >= 2)) //2022.2 and up
            {
                flags = reader.ReadInt32();
            }
        }
    }

    public class PPtrKeyframe
    {
        public float time;
        public PPtr<Object> value;

        public PPtrKeyframe(float time, PPtr<Object> value)
        {
            this.time = time;
            this.value = value;
        }

        public PPtrKeyframe(ObjectReader reader)
        {
            time = reader.ReadSingle();
            value = new PPtr<Object>(reader);
        }
    }

    public class PPtrCurve
    {
        public PPtrKeyframe[] curve = Array.Empty<PPtrKeyframe>();
        public string attribute = string.Empty;
        public string path = string.Empty;
        public int classID;
        public PPtr<MonoScript>? script;
        public int flags;

        public PPtrCurve() { }

        public PPtrCurve(ObjectReader reader)
        {
            var version = reader.version;
            int numCurves = reader.ReadInt32();
            reader.CheckArrayLength(numCurves);
            curve = new PPtrKeyframe[numCurves];
            for (int i = 0; i < numCurves; i++)
            {
                curve[i] = new PPtrKeyframe(reader);
            }

            attribute = reader.ReadAlignedString();
            path = reader.ReadAlignedString();
            classID = reader.ReadInt32();
            script = new PPtr<MonoScript>(reader);
            if (version[0] > 2022 || (version[0] == 2022 && version[1] >= 2)) //2022.2 and up
            {
                flags = reader.ReadInt32();
            }
        }
    }

    public class AABB
    {
        public Vector3 m_Center;
        public Vector3 m_Extent;

        public AABB(ObjectReader reader)
        {
            m_Center = reader.ReadVector3();
            m_Extent = reader.ReadVector3();
        }
    }

    public class XForm
    {
        public Vector3 t;
        public Quaternion q;
        public Vector3 s;

        public XForm(ObjectReader reader)
        {
            var version = reader.version;
            t = version[0] > 5 || (version[0] == 5 && version[1] >= 4) ? reader.ReadVector3() : (Vector3)reader.ReadVector4();//5.4 and up
            q = reader.ReadQuaternion();
            s = version[0] > 5 || (version[0] == 5 && version[1] >= 4) ? reader.ReadVector3() : (Vector3)reader.ReadVector4();//5.4 and up
        }
    }

    public class HandPose
    {
        public XForm m_GrabX;
        public float[] m_DoFArray;
        public float m_Override;
        public float m_CloseOpen;
        public float m_InOut;
        public float m_Grab;

        public HandPose(ObjectReader reader)
        {
            m_GrabX = new XForm(reader);
            m_DoFArray = reader.ReadSingleArray();
            m_Override = reader.ReadSingle();
            m_CloseOpen = reader.ReadSingle();
            m_InOut = reader.ReadSingle();
            m_Grab = reader.ReadSingle();
        }
    }

    public class HumanGoal
    {
        public XForm m_X;
        public float m_WeightT;
        public float m_WeightR;
        public Vector3 m_HintT;
        public float m_HintWeightT;

        public HumanGoal(ObjectReader reader)
        {
            var version = reader.version;
            m_X = new XForm(reader);
            m_WeightT = reader.ReadSingle();
            m_WeightR = reader.ReadSingle();
            if (version[0] >= 5)//5.0 and up
            {
                m_HintT = version[0] > 5 || (version[0] == 5 && version[1] >= 4) ? reader.ReadVector3() : (Vector3)reader.ReadVector4();//5.4 and up
                m_HintWeightT = reader.ReadSingle();
            }
        }
    }

    public class HumanPose
    {
        public XForm m_RootX;
        public Vector3 m_LookAtPosition;
        public Vector4 m_LookAtWeight;
        public HumanGoal[] m_GoalArray;
        public HandPose m_LeftHandPose;
        public HandPose m_RightHandPose;
        public float[] m_DoFArray;
        public Vector3[] m_TDoFArray = Array.Empty<Vector3>();

        public HumanPose(ObjectReader reader)
        {
            var version = reader.version;
            m_RootX = new XForm(reader);
            m_LookAtPosition = version[0] > 5 || (version[0] == 5 && version[1] >= 4) ? reader.ReadVector3() : (Vector3)reader.ReadVector4();//5.4 and up
            m_LookAtWeight = reader.ReadVector4();

            int numGoals = reader.ReadInt32();
            reader.CheckArrayLength(numGoals);
            m_GoalArray = new HumanGoal[numGoals];
            for (int i = 0; i < numGoals; i++)
            {
                m_GoalArray[i] = new HumanGoal(reader);
            }

            m_LeftHandPose = new HandPose(reader);
            m_RightHandPose = new HandPose(reader);

            m_DoFArray = reader.ReadSingleArray();

            if (version[0] > 5 || (version[0] == 5 && version[1] >= 2))//5.2 and up
            {
                int numTDof = reader.ReadInt32();
                reader.CheckArrayLength(numTDof);
                m_TDoFArray = new Vector3[numTDof];
                for (int i = 0; i < numTDof; i++)
                {
                    m_TDoFArray[i] = version[0] > 5 || (version[0] == 5 && version[1] >= 4) ? reader.ReadVector3() : (Vector3)reader.ReadVector4();//5.4 and up
                }
            }
        }
    }

    public class StreamedClip
    {
        public uint[] data;
        public uint curveCount;

        public StreamedClip(ObjectReader reader)
        {
            data = reader.ReadUInt32Array();
            curveCount = reader.ReadUInt32();
        }

        public class StreamedCurveKey
        {
            public int index;
            public float[] coeff;

            public float value;
            public float outSlope;
            public float inSlope;

            public StreamedCurveKey(BinaryReader reader)
            {
                index = reader.ReadInt32();
                coeff = reader.ReadSingleArray(4);

                outSlope = coeff[2];
                value = coeff[3];
            }

            public float CalculateNextInSlope(float dx, StreamedCurveKey rhs)
            {
                //Stepped
                if (coeff[0] == 0f && coeff[1] == 0f && coeff[2] == 0f)
                {
                    return float.PositiveInfinity;
                }

                dx = Math.Max(dx, 0.0001f);
                var dy = rhs.value - value;
                var length = 1.0f / (dx * dx);
                var d1 = outSlope * dx;
                var d2 = dy + dy + dy - d1 - d1 - coeff[1] / length;
                return d2 / dx;
            }
        }

        public class StreamedFrame
        {
            public float time;
            public StreamedCurveKey[] keyList;

            public StreamedFrame(BinaryReader reader)
            {
                time = reader.ReadSingle();

                int numKeys = reader.ReadInt32();
                reader.CheckArrayLength(numKeys);
                keyList = new StreamedCurveKey[numKeys];
                for (int i = 0; i < numKeys; i++)
                {
                    keyList[i] = new StreamedCurveKey(reader);
                }
            }
        }

        public List<StreamedFrame> ReadData()
        {
            var frameList = new List<StreamedFrame>();
            var buffer = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, buffer, 0, buffer.Length);
            using (var reader = new BinaryReader(new MemoryStream(buffer)))
            {
                while (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    frameList.Add(new StreamedFrame(reader));
                }
            }

            // Track the most recent keyframe per curve index in O(totalKeys) instead of O(frames^2 * keys)
            if (frameList.Count > 3)
            {
                var lastKeyByIndex = new Dictionary<int, (float Time, StreamedCurveKey Key)>();
                int seedFrames = Math.Min(2, frameList.Count);
                for (int f = 0; f < seedFrames; f++)
                {
                    var seedFrame = frameList[f];
                    for (int k = seedFrame.keyList.Length - 1; k >= 0; k--)
                    {
                        var ck = seedFrame.keyList[k];
                        lastKeyByIndex[ck.index] = (seedFrame.time, ck);
                    }
                }

                for (int frameIndex = 2; frameIndex < frameList.Count - 1; frameIndex++)
                {
                    var frame = frameList[frameIndex];
                    var keys = frame.keyList;
                    for (int k = 0; k < keys.Length; k++)
                    {
                        var curveKey = keys[k];
                        if (lastKeyByIndex.TryGetValue(curveKey.index, out var prev))
                        {
                            curveKey.inSlope = prev.Key.CalculateNextInSlope(frame.time - prev.Time, curveKey);
                        }
                    }

                    // Walk backwards so the first key for a duplicate curve index in this frame wins (matching FirstOrDefault).
                    for (int k = keys.Length - 1; k >= 0; k--)
                    {
                        var ck = keys[k];
                        lastKeyByIndex[ck.index] = (frame.time, ck);
                    }
                }
            }
            return frameList;
        }
    }

    public class DenseClip
    {
        public int m_FrameCount;
        public uint m_CurveCount;
        public float m_SampleRate;
        public float m_BeginTime;
        public float[] m_SampleArray;

        public DenseClip(ObjectReader reader)
        {
            m_FrameCount = reader.ReadInt32();
            m_CurveCount = reader.ReadUInt32();
            m_SampleRate = reader.ReadSingle();
            m_BeginTime = reader.ReadSingle();
            m_SampleArray = reader.ReadSingleArray();
        }
    }

    public class ConstantClip
    {
        public float[] data;

        public ConstantClip(ObjectReader reader)
        {
            data = reader.ReadSingleArray();
        }
    }

    public class ValueConstant
    {
        public uint m_ID;
        public uint m_TypeID;
        public uint m_Type;
        public uint m_Index;

        public ValueConstant(ObjectReader reader)
        {
            var version = reader.version;
            m_ID = reader.ReadUInt32();
            if (version[0] < 5 || (version[0] == 5 && version[1] < 5))//5.5 down
            {
                m_TypeID = reader.ReadUInt32();
            }
            m_Type = reader.ReadUInt32();
            m_Index = reader.ReadUInt32();
        }
    }

    public class ValueArrayConstant
    {
        public ValueConstant[] m_ValueArray;

        public ValueArrayConstant(ObjectReader reader)
        {
            int numVals = reader.ReadInt32();
            reader.CheckArrayLength(numVals);
            m_ValueArray = new ValueConstant[numVals];
            for (int i = 0; i < numVals; i++)
            {
                m_ValueArray[i] = new ValueConstant(reader);
            }
        }
    }

    public class Clip
    {
        public StreamedClip m_StreamedClip;
        public DenseClip m_DenseClip;
        public ConstantClip? m_ConstantClip;
        public ValueArrayConstant? m_Binding;

        public Clip(ObjectReader reader)
        {
            var version = reader.version;
            m_StreamedClip = new StreamedClip(reader);
            m_DenseClip = new DenseClip(reader);
            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up
            {
                m_ConstantClip = new ConstantClip(reader);
            }
            if (version[0] < 2018 || (version[0] == 2018 && version[1] < 3)) //2018.3 down
            {
                m_Binding = new ValueArrayConstant(reader);
            }
        }

        public AnimationClipBindingConstant ConvertValueArrayToGenericBinding()
        {
            var bindings = new AnimationClipBindingConstant();
            var genericBindings = new List<GenericBinding>();
            var values = m_Binding;
            if (values != null)
            {
                for (int i = 0; i < values.m_ValueArray.Length;)
                {
                    var curveID = values.m_ValueArray[i].m_ID;
                    var curveTypeID = values.m_ValueArray[i].m_TypeID;
                    var binding = new GenericBinding();
                    genericBindings.Add(binding);
                    if (curveTypeID == 4174552735) //CRC(PositionX))
                    {
                        binding.path = curveID;
                        binding.attribute = 1; //kBindTransformPosition
                        binding.typeID = ClassIDType.Transform;
                        i += 3;
                    }
                    else if (curveTypeID == 2211994246) //CRC(QuaternionX))
                    {
                        binding.path = curveID;
                        binding.attribute = 2; //kBindTransformRotation
                        binding.typeID = ClassIDType.Transform;
                        i += 4;
                    }
                    else if (curveTypeID == 1512518241) //CRC(ScaleX))
                    {
                        binding.path = curveID;
                        binding.attribute = 3; //kBindTransformScale
                        binding.typeID = ClassIDType.Transform;
                        i += 3;
                    }
                    else
                    {
                        binding.typeID = ClassIDType.Animator;
                        binding.path = 0;
                        binding.attribute = curveID;
                        i++;
                    }
                }
            }
            bindings.genericBindings = genericBindings.ToArray();
            return bindings;
        }
    }

    public class ValueDelta
    {
        public float m_Start;
        public float m_Stop;

        public ValueDelta(ObjectReader reader)
        {
            m_Start = reader.ReadSingle();
            m_Stop = reader.ReadSingle();
        }
    }

    public class ClipMuscleConstant
    {
        public HumanPose m_DeltaPose;
        public XForm m_StartX;
        public XForm? m_StopX;
        public XForm m_LeftFootStartX;
        public XForm m_RightFootStartX;
        public XForm? m_MotionStartX;
        public XForm? m_MotionStopX;
        public Vector3 m_AverageSpeed;
        public Clip m_Clip;
        public float m_StartTime;
        public float m_StopTime;
        public float m_OrientationOffsetY;
        public float m_Level;
        public float m_CycleOffset;
        public float m_AverageAngularSpeed;
        public int[] m_IndexArray;
        public ValueDelta[] m_ValueArrayDelta;
        public float[] m_ValueArrayReferencePose = Array.Empty<float>();
        public bool m_Mirror;
        public bool m_LoopTime;
        public bool m_LoopBlend;
        public bool m_LoopBlendOrientation;
        public bool m_LoopBlendPositionY;
        public bool m_LoopBlendPositionXZ;
        public bool m_StartAtOrigin;
        public bool m_KeepOriginalOrientation;
        public bool m_KeepOriginalPositionY;
        public bool m_KeepOriginalPositionXZ;
        public bool m_HeightFromFeet;

        public ClipMuscleConstant(ObjectReader reader)
        {
            var version = reader.version;
            m_DeltaPose = new HumanPose(reader);
            m_StartX = new XForm(reader);
            if (version[0] > 5 || (version[0] == 5 && version[1] >= 5))//5.5 and up
            {
                m_StopX = new XForm(reader);
            }
            m_LeftFootStartX = new XForm(reader);
            m_RightFootStartX = new XForm(reader);
            if (version[0] < 5)//5.0 down
            {
                m_MotionStartX = new XForm(reader);
                m_MotionStopX = new XForm(reader);
            }
            m_AverageSpeed = version[0] > 5 || (version[0] == 5 && version[1] >= 4) ? reader.ReadVector3() : (Vector3)reader.ReadVector4();//5.4 and up
            m_Clip = new Clip(reader);
            m_StartTime = reader.ReadSingle();
            m_StopTime = reader.ReadSingle();
            m_OrientationOffsetY = reader.ReadSingle();
            m_Level = reader.ReadSingle();
            m_CycleOffset = reader.ReadSingle();
            m_AverageAngularSpeed = reader.ReadSingle();

            m_IndexArray = reader.ReadInt32Array();
            if (version[0] < 4 || (version[0] == 4 && version[1] < 3)) //4.3 down
            {
                var m_AdditionalCurveIndexArray = reader.ReadInt32Array();
            }
            int numDeltas = reader.ReadInt32();
            reader.CheckArrayLength(numDeltas);
            m_ValueArrayDelta = new ValueDelta[numDeltas];
            for (int i = 0; i < numDeltas; i++)
            {
                m_ValueArrayDelta[i] = new ValueDelta(reader);
            }
            if (version[0] > 5 || (version[0] == 5 && version[1] >= 3))//5.3 and up
            {
                m_ValueArrayReferencePose = reader.ReadSingleArray();
            }

            m_Mirror = reader.ReadBoolean();
            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up
            {
                m_LoopTime = reader.ReadBoolean();
            }
            m_LoopBlend = reader.ReadBoolean();
            m_LoopBlendOrientation = reader.ReadBoolean();
            m_LoopBlendPositionY = reader.ReadBoolean();
            m_LoopBlendPositionXZ = reader.ReadBoolean();
            if (version[0] > 5 || (version[0] == 5 && version[1] >= 5))//5.5 and up
            {
                m_StartAtOrigin = reader.ReadBoolean();
            }
            m_KeepOriginalOrientation = reader.ReadBoolean();
            m_KeepOriginalPositionY = reader.ReadBoolean();
            m_KeepOriginalPositionXZ = reader.ReadBoolean();
            m_HeightFromFeet = reader.ReadBoolean();
            reader.AlignStream();
        }
    }

    public class GenericBinding
    {
        public uint path;
        public uint attribute;
        public PPtr<Object>? script;
        public ClassIDType typeID;
        public byte customType;
        public byte isPPtrCurve;
        public byte isIntCurve;
        public byte isSerializeReferenceCurve;

        public GenericBinding() { }

        public GenericBinding(ObjectReader reader)
        {
            var version = reader.version;
            path = reader.ReadUInt32();
            attribute = reader.ReadUInt32();
            script = new PPtr<Object>(reader);
            if (version[0] > 5 || (version[0] == 5 && version[1] >= 6)) //5.6 and up
            {
                typeID = (ClassIDType)reader.ReadInt32();
            }
            else
            {
                typeID = (ClassIDType)reader.ReadUInt16();
            }
            customType = reader.ReadByte();
            isPPtrCurve = reader.ReadByte();
            if (version[0] > 2022 || (version[0] == 2022 && version[1] >= 1)) //2022.1 and up
            {
                isIntCurve = reader.ReadByte();
            }
            if (version[0] > 2022 || (version[0] == 2022 && version[1] >= 2)) //2022.2 and up
            {
                isSerializeReferenceCurve = reader.ReadByte();
            }
            reader.AlignStream();
        }
    }

    public class AnimationClipBindingConstant
    {
        public GenericBinding[] genericBindings = Array.Empty<GenericBinding>();
        public PPtr<Object>[] pptrCurveMapping = Array.Empty<PPtr<Object>>();

        public AnimationClipBindingConstant() { }

        public AnimationClipBindingConstant(ObjectReader reader)
        {
            int numBindings = reader.ReadInt32();
            reader.CheckArrayLength(numBindings);
            genericBindings = new GenericBinding[numBindings];
            for (int i = 0; i < numBindings; i++)
            {
                genericBindings[i] = new GenericBinding(reader);
            }

            int numMappings = reader.ReadInt32();
            reader.CheckArrayLength(numMappings);
            pptrCurveMapping = new PPtr<Object>[numMappings];
            for (int i = 0; i < numMappings; i++)
            {
                pptrCurveMapping[i] = new PPtr<Object>(reader);
            }
        }

        public GenericBinding? FindBinding(int index)
        {
            int curves = 0;
            foreach (var b in genericBindings)
            {
                if (b.typeID == ClassIDType.Transform)
                {
                    switch (b.attribute)
                    {
                        case 1: //kBindTransformPosition
                        case 3: //kBindTransformScale
                        case 4: //kBindTransformEuler
                            curves += 3;
                            break;
                        case 2: //kBindTransformRotation
                            curves += 4;
                            break;
                        default:
                            curves += 1;
                            break;
                    }
                }
                else
                {
                    curves += 1;
                }
                if (curves > index)
                {
                    return b;
                }
            }

            return null;
        }
    }

    public class AnimationEvent
    {
        public float time;
        public string functionName;
        public string data;
        public PPtr<Object> objectReferenceParameter;
        public float floatParameter;
        public int intParameter;
        public int messageOptions;

        public AnimationEvent(ObjectReader reader)
        {
            var version = reader.version;

            time = reader.ReadSingle();
            functionName = reader.ReadAlignedString();
            data = reader.ReadAlignedString();
            objectReferenceParameter = new PPtr<Object>(reader);
            floatParameter = reader.ReadSingle();
            if (version[0] >= 3) //3 and up
            {
                intParameter = reader.ReadInt32();
            }
            messageOptions = reader.ReadInt32();
        }
    }

    public enum AnimationType
    {
        Legacy = 1,
        Generic = 2,
        Humanoid = 3
    };

    public sealed class AnimationClip : NamedObject
    {
        public AnimationType m_AnimationType;
        public bool m_Legacy;
        public bool m_Compressed;
        public bool m_UseHighQualityCurve;
        public QuaternionCurve[] m_RotationCurves;
        public CompressedAnimationCurve[] m_CompressedRotationCurves;
        public Vector3Curve[] m_EulerCurves = Array.Empty<Vector3Curve>();
        public Vector3Curve[] m_PositionCurves;
        public Vector3Curve[] m_ScaleCurves;
        public FloatCurve[] m_FloatCurves;
        public PPtrCurve[] m_PPtrCurves = Array.Empty<PPtrCurve>();
        public float m_SampleRate;
        public int m_WrapMode;
        public AABB? m_Bounds;
        public uint m_MuscleClipSize;
        public ClipMuscleConstant? m_MuscleClip;
        public AnimationClipBindingConstant? m_ClipBindingConstant;
        public bool m_HasGenericRootTransform;
        public bool m_HasMotionFloatCurves;
        public AnimationEvent[] m_Events;


        public AnimationClip(ObjectReader reader) : base(reader)
        {
            if (version[0] >= 5)//5.0 and up
            {
                m_Legacy = reader.ReadBoolean();
            }
            else if (version[0] >= 4)//4.0 and up
            {
                m_AnimationType = (AnimationType)reader.ReadInt32();
                if (m_AnimationType == AnimationType.Legacy)
                    m_Legacy = true;
            }
            else
            {
                m_Legacy = true;
            }
            m_Compressed = reader.ReadBoolean();
            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3))//4.3 and up
            {
                m_UseHighQualityCurve = reader.ReadBoolean();
            }
            reader.AlignStream();
            int numRCurves = reader.ReadInt32();
            reader.CheckArrayLength(numRCurves);
            m_RotationCurves = new QuaternionCurve[numRCurves];
            for (int i = 0; i < numRCurves; i++)
            {
                m_RotationCurves[i] = new QuaternionCurve(reader);
            }

            int numCRCurves = reader.ReadInt32();
            reader.CheckArrayLength(numCRCurves);
            m_CompressedRotationCurves = new CompressedAnimationCurve[numCRCurves];
            for (int i = 0; i < numCRCurves; i++)
            {
                m_CompressedRotationCurves[i] = new CompressedAnimationCurve(reader);
            }

            if (version[0] > 5 || (version[0] == 5 && version[1] >= 3))//5.3 and up
            {
                int numEulerCurves = reader.ReadInt32();
                reader.CheckArrayLength(numEulerCurves);
                m_EulerCurves = new Vector3Curve[numEulerCurves];
                for (int i = 0; i < numEulerCurves; i++)
                {
                    m_EulerCurves[i] = new Vector3Curve(reader);
                }
            }

            int numPCurves = reader.ReadInt32();
            reader.CheckArrayLength(numPCurves);
            m_PositionCurves = new Vector3Curve[numPCurves];
            for (int i = 0; i < numPCurves; i++)
            {
                m_PositionCurves[i] = new Vector3Curve(reader);
            }

            int numSCurves = reader.ReadInt32();
            reader.CheckArrayLength(numSCurves);
            m_ScaleCurves = new Vector3Curve[numSCurves];
            for (int i = 0; i < numSCurves; i++)
            {
                m_ScaleCurves[i] = new Vector3Curve(reader);
            }

            int numFCurves = reader.ReadInt32();
            reader.CheckArrayLength(numFCurves);
            m_FloatCurves = new FloatCurve[numFCurves];
            for (int i = 0; i < numFCurves; i++)
            {
                m_FloatCurves[i] = new FloatCurve(reader);
            }

            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up
            {
                int numPtrCurves = reader.ReadInt32();
                reader.CheckArrayLength(numPtrCurves);
                m_PPtrCurves = new PPtrCurve[numPtrCurves];
                for (int i = 0; i < numPtrCurves; i++)
                {
                    m_PPtrCurves[i] = new PPtrCurve(reader);
                }
            }

            m_SampleRate = reader.ReadSingle();
            m_WrapMode = reader.ReadInt32();
            if (version[0] > 3 || (version[0] == 3 && version[1] >= 4)) //3.4 and up
            {
                m_Bounds = new AABB(reader);
            }
            if (version[0] >= 4)//4.0 and up
            {
                m_MuscleClipSize = reader.ReadUInt32();
                m_MuscleClip = new ClipMuscleConstant(reader);
            }
            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up
            {
                m_ClipBindingConstant = new AnimationClipBindingConstant(reader);
            }
            if (version[0] > 2018 || (version[0] == 2018 && version[1] >= 3)) //2018.3 and up
            {
                m_HasGenericRootTransform = reader.ReadBoolean();
                m_HasMotionFloatCurves = reader.ReadBoolean();
                reader.AlignStream();
            }
            int numEvents = reader.ReadInt32();
            reader.CheckArrayLength(numEvents);
            m_Events = new AnimationEvent[numEvents];
            for (int i = 0; i < numEvents; i++)
            {
                m_Events[i] = new AnimationEvent(reader);
            }
            if (version[0] >= 2017) //2017 and up
            {
                reader.AlignStream();
            }
            ReconstructPPtrCurves();
        }

        public void ReconstructPPtrCurves()
        {
            if (m_ClipBindingConstant == null || m_MuscleClip == null || m_MuscleClip.m_Clip == null)
            {
                return;
            }

            // If m_PPtrCurves already has items, no need to reconstruct
            if (m_PPtrCurves != null && m_PPtrCurves.Length > 0)
            {
                return;
            }

            // Skip expensive StreamedClip/DenseClip decoding when the clip has no PPtr curves
            if (m_ClipBindingConstant.pptrCurveMapping == null
                || m_ClipBindingConstant.pptrCurveMapping.Length == 0
                || m_ClipBindingConstant.genericBindings == null
                || !m_ClipBindingConstant.genericBindings.Any(b => b.isPPtrCurve == 1))
            {
                return;
            }

            var m_Clip = m_MuscleClip.m_Clip;
            
            // We want to find all generic bindings where isPPtrCurve == 1
            // Let's create a dictionary to collect keyframes for each PPtr curve binding
            var curveDict = new Dictionary<GenericBinding, List<PPtrKeyframe>>();

            // Helper to add a keyframe
            void AddPPtrKeyframe(int index, float time, float[] data, int offset, ref int curveIndex)
            {
                var binding = m_ClipBindingConstant.FindBinding(index);
                if (binding == null)
                {
                    curveIndex++;
                    return;
                }

                if (binding.isPPtrCurve == 1)
                {
                    if (!curveDict.TryGetValue(binding, out var list))
                    {
                        list = new List<PPtrKeyframe>();
                        curveDict[binding] = list;
                    }

                    var val = data[curveIndex + offset];
                    int pptrIndex = (int)Math.Round(val);
                    if (m_ClipBindingConstant.pptrCurveMapping != null && pptrIndex >= 0 && pptrIndex < m_ClipBindingConstant.pptrCurveMapping.Length)
                    {
                        var pptr = m_ClipBindingConstant.pptrCurveMapping[pptrIndex];
                        var kf = new PPtrKeyframe(time < 0f ? 0f : time, pptr);
                        list.Add(kf);
                    }
                    curveIndex++;
                }
                else
                {
                    // Skip other curve types
                    if (binding.typeID == ClassIDType.Transform)
                    {
                        switch (binding.attribute)
                        {
                            case 1: // Position
                            case 3: // Scale
                            case 4: // Euler
                                curveIndex += 3;
                                break;
                            case 2: // Rotation
                                curveIndex += 4;
                                break;
                            default:
                                curveIndex += 1;
                                break;
                        }
                    }
                    else
                    {
                        curveIndex += 1;
                    }
                }
            }

            // Read StreamedClip
            if (m_Clip.m_StreamedClip != null)
            {
                try
                {
                    var streamedFrames = m_Clip.m_StreamedClip.ReadData();
                    for (int frameIndex = 0; frameIndex < streamedFrames.Count - 1; frameIndex++)
                    {
                        var frame = streamedFrames[frameIndex];
                        var streamedValues = frame.keyList.Select(x => x.value).ToArray();
                        for (int curveIndex = 0; curveIndex < frame.keyList.Length;)
                        {
                            AddPPtrKeyframe(frame.keyList[curveIndex].index, frame.time, streamedValues, 0, ref curveIndex);
                        }
                    }
                }
                catch
                {
                    // Ignore decoding errors
                }
            }

            // Read DenseClip
            if (m_Clip.m_DenseClip != null)
            {
                var m_DenseClip = m_Clip.m_DenseClip;
                var streamCount = m_Clip.m_StreamedClip?.curveCount ?? 0;
                for (int frameIndex = 0; frameIndex < m_DenseClip.m_FrameCount; frameIndex++)
                {
                    var time = m_DenseClip.m_BeginTime + frameIndex / m_DenseClip.m_SampleRate;
                    var frameOffset = frameIndex * m_DenseClip.m_CurveCount;
                    for (int curveIndex = 0; curveIndex < m_DenseClip.m_CurveCount;)
                    {
                        var index = streamCount + curveIndex;
                        AddPPtrKeyframe((int)index, time, m_DenseClip.m_SampleArray, (int)frameOffset, ref curveIndex);
                    }
                }
            }

            // Read ConstantClip
            if (m_Clip.m_ConstantClip != null)
            {
                var m_ConstantClip = m_Clip.m_ConstantClip;
                var denseCount = m_Clip.m_DenseClip?.m_CurveCount ?? 0;
                var streamCount = m_Clip.m_StreamedClip?.curveCount ?? 0;
                var time2 = 0.0f;
                for (int i = 0; i < 2; i++)
                {
                    for (int curveIndex = 0; curveIndex < m_ConstantClip.data.Length;)
                    {
                        var index = streamCount + denseCount + curveIndex;
                        AddPPtrKeyframe((int)index, time2, m_ConstantClip.data, 0, ref curveIndex);
                    }
                    if (m_MuscleClip != null)
                        time2 = m_MuscleClip.m_StopTime;
                }
            }

            // Now construct m_PPtrCurves array
            var pptrCurves = new List<PPtrCurve>();
            foreach (var kvp in curveDict)
            {
                var binding = kvp.Key;
                var keyframes = kvp.Value;

                if (keyframes.Count > 0)
                {
                    var pptrCurve = new PPtrCurve
                    {
                        curve = keyframes.ToArray(),
                        attribute = binding.attribute == 2015549526 ? "m_Sprite" : $"unknown_{binding.attribute}",
                        path = binding.path == 0 ? "" : $"path_{binding.path}",
                        classID = (int)binding.typeID,
                        script = binding.script != null ? new PPtr<MonoScript>(binding.script.m_FileID, binding.script.m_PathID, assetsFile) : null
                    };
                    pptrCurves.Add(pptrCurve);
                }
            }

            m_PPtrCurves = pptrCurves.ToArray();
        }
    }
}
