using System;
using System.Collections;
using System.IO;

namespace AssetStudio
{
    public sealed class Camera : Behaviour
    {
        public uint m_ClearFlags = 1;
        public Color m_BackGroundColor = new Color(0.19215687f, 0.3019608f, 0.4745098f, 0f);
        public int m_ProjectionMatrixMode;
        public int m_GateFitMode = 2;
        public Vector2 m_SensorSize = new Vector2(36f, 24f);
        public Vector2 m_LensShift;
        public float m_FocalLength = 50f;
        public Vector4 m_NormalizedViewPortRect = new Vector4(0f, 0f, 1f, 1f);
        public float m_NearClipPlane = 0.3f;
        public float m_FarClipPlane = 1000f;
        public float m_FieldOfView = 60f;
        public bool m_Orthographic;
        public float m_OrthographicSize = 5f;
        public float m_Depth;
        public uint m_CullingMask = uint.MaxValue;
        public int m_RenderingPath = -1;
        public bool IsTruncated { get; private set; }

        public bool Enabled => m_Enabled != 0;

        public Camera(ObjectReader reader) : base(reader)
        {
            if (TryReadFromTypeTree(reader))
            {
                return;
            }

            try
            {
                EnsureRemainingBytes(reader, 20); // m_ClearFlags (4) + m_BackGroundColor (16)
                m_ClearFlags = reader.ReadUInt32();
                m_BackGroundColor = reader.ReadColor4();

                bool is2018_2OrUp = IsVersionAtLeast(2018, 2);
                bool is2019_1OrUp = IsVersionAtLeast(2019, 1);
                bool is2022_2OrUp = IsVersionAtLeast(2022, 2);

                if (is2018_2OrUp)
                {
                    EnsureRemainingBytes(reader, 4);
                    m_ProjectionMatrixMode = reader.ReadInt32();
                }

                if (is2019_1OrUp)
                {
                    EnsureRemainingBytes(reader, 4);
                    m_GateFitMode = reader.ReadInt32();
                    if (platform == BuildTarget.NoTarget)
                    {
                        EnsureRemainingBytes(reader, 4);
                        _ = reader.ReadInt32(); // m_FOVAxisMode (editor-only)
                    }
                }

                if (is2022_2OrUp)
                {
                    EnsureRemainingBytes(reader, 56);
                    _ = reader.ReadInt32();   // m_Iso
                    _ = reader.ReadSingle();  // m_ShutterSpeed
                    _ = reader.ReadSingle();  // m_Aperture
                    _ = reader.ReadSingle();  // m_FocusDistance
                    m_FocalLength = reader.ReadSingle();
                    _ = reader.ReadInt32();   // m_BladeCount
                    _ = reader.ReadVector2(); // m_Curvature
                    _ = reader.ReadSingle();  // m_BarrelClipping
                    _ = reader.ReadSingle();  // m_Anamorphism
                    m_SensorSize = reader.ReadVector2();
                    m_LensShift = reader.ReadVector2();
                }
                else if (is2018_2OrUp)
                {
                    EnsureRemainingBytes(reader, 20);
                    m_SensorSize = reader.ReadVector2();
                    m_LensShift = reader.ReadVector2();
                    m_FocalLength = reader.ReadSingle();
                }

                // Core projection & clipping block (consistent across Unity 3.x–6000.x)
                EnsureRemainingBytes(reader, 40);
                m_NormalizedViewPortRect = reader.ReadVector4();
                m_NearClipPlane = reader.ReadSingle();
                m_FarClipPlane = reader.ReadSingle();
                m_FieldOfView = reader.ReadSingle();
                m_Orthographic = reader.ReadBoolean();
                reader.AlignStream();
                m_OrthographicSize = reader.ReadSingle();
                m_Depth = reader.ReadSingle();

                if (RemainingObjectBytes(reader) >= 4)
                {
                    m_CullingMask = reader.ReadUInt32();
                }

                if (RemainingObjectBytes(reader) >= 4)
                {
                    m_RenderingPath = reader.ReadInt32();
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                Logger.Warning($"Camera (PathID {m_PathID}): failed to read fields ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private bool TryReadFromTypeTree(ObjectReader reader)
        {
            var typeTree = serializedType?.m_Type;
            if (typeTree?.m_Nodes == null || typeTree.m_Nodes.Count == 0)
            {
                return false;
            }

            var savedPosition = reader.Position;
            try
            {
                var dict = TypeTreeHelper.ReadType(typeTree, reader);
                if (dict == null)
                {
                    reader.Position = savedPosition;
                    return false;
                }

                if (TryReadByte(dict, "m_Enabled", out var enabledByte))
                {
                    m_Enabled = enabledByte;
                }

                if (TryReadUInt(dict, "m_ClearFlags", out var clearFlags))
                {
                    m_ClearFlags = clearFlags;
                }

                if (TryReadColor(dict, "m_BackGroundColor", out var bgColor))
                {
                    m_BackGroundColor = bgColor;
                }

                if (TryReadInt(dict, "m_projectionMatrixMode", out var projMode))
                {
                    m_ProjectionMatrixMode = projMode;
                }

                if (TryReadInt(dict, "m_GateFitMode", out var gateFit))
                {
                    m_GateFitMode = gateFit;
                }

                if (TryReadVector2(dict, "m_SensorSize", out var sensorSize))
                {
                    m_SensorSize = sensorSize;
                }

                if (TryReadVector2(dict, "m_LensShift", out var lensShift))
                {
                    m_LensShift = lensShift;
                }

                if (TryReadFloat(dict, "m_FocalLength", out var focalLength))
                {
                    m_FocalLength = focalLength;
                }

                if (TryReadRect(dict, "m_NormalizedViewPortRect", out var viewport))
                {
                    m_NormalizedViewPortRect = viewport;
                }

                if (TryReadFloat(dict, "near clip plane", out var nearClip))
                {
                    m_NearClipPlane = nearClip;
                }

                if (TryReadFloat(dict, "far clip plane", out var farClip))
                {
                    m_FarClipPlane = farClip;
                }

                if (TryReadFloat(dict, "field of view", out var fov))
                {
                    m_FieldOfView = fov;
                }

                if (TryReadBool(dict, "orthographic", out var ortho))
                {
                    m_Orthographic = ortho;
                }

                if (TryReadFloat(dict, "orthographic size", out var orthoSize))
                {
                    m_OrthographicSize = orthoSize;
                }

                if (TryReadFloat(dict, "m_Depth", out var depth))
                {
                    m_Depth = depth;
                }

                if (dict.Contains("m_CullingMask"))
                {
                    if (dict["m_CullingMask"] is IDictionary maskDict && TryReadUInt(maskDict, "m_Bits", out var bits))
                    {
                        m_CullingMask = bits;
                    }
                    else if (TryReadUInt(dict, "m_CullingMask", out var rawMask))
                    {
                        m_CullingMask = rawMask;
                    }
                }

                if (TryReadInt(dict, "m_RenderingPath", out var renderingPath))
                {
                    m_RenderingPath = renderingPath;
                }

                return true;
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                Logger.Warning($"Camera (PathID {m_PathID}): TypeTree read failed ({ex.GetType().Name}: {ex.Message}).");
                return true;
            }
        }

        private bool IsVersionAtLeast(int major, int minor)
        {
            if (version == null || version.Length == 0)
            {
                return false;
            }

            if (version[0] != major)
            {
                return version[0] > major;
            }

            return version.Length > 1 && version[1] >= minor;
        }

        private static bool TryReadColor(IDictionary dict, string key, out Color value)
        {
            value = default;
            if (!dict.Contains(key) || dict[key] is not IDictionary c)
            {
                return false;
            }

            TryReadFloat(c, "r", out float r);
            TryReadFloat(c, "g", out float g);
            TryReadFloat(c, "b", out float b);
            TryReadFloat(c, "a", out float a);
            value = new Color(r, g, b, a);
            return true;
        }

        private static bool TryReadVector2(IDictionary dict, string key, out Vector2 value)
        {
            value = Vector2.Zero;
            if (!dict.Contains(key) || dict[key] is not IDictionary v)
            {
                return false;
            }

            TryReadFloat(v, "x", out float x);
            TryReadFloat(v, "y", out float y);
            value = new Vector2(x, y);
            return true;
        }

        private static bool TryReadRect(IDictionary dict, string key, out Vector4 value)
        {
            value = new Vector4(0f, 0f, 1f, 1f);
            if (!dict.Contains(key) || dict[key] is not IDictionary r)
            {
                return false;
            }

            TryReadFloat(r, "x", out float x);
            TryReadFloat(r, "y", out float y);
            TryReadFloat(r, "width", out float w);
            TryReadFloat(r, "height", out float h);
            value = new Vector4(x, y, w, h);
            return true;
        }

        private static bool TryReadFloat(IDictionary dict, string key, out float value)
        {
            value = 0f;
            if (!dict.Contains(key) || dict[key] == null) return false;
            try
            {
                value = Convert.ToSingle(dict[key]);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadInt(IDictionary dict, string key, out int value)
        {
            value = 0;
            if (!dict.Contains(key) || dict[key] == null) return false;
            try
            {
                value = Convert.ToInt32(dict[key]);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadUInt(IDictionary dict, string key, out uint value)
        {
            value = 0;
            if (!dict.Contains(key) || dict[key] == null) return false;
            try
            {
                value = unchecked((uint)Convert.ToInt64(dict[key]));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadByte(IDictionary dict, string key, out byte value)
        {
            value = 0;
            if (!dict.Contains(key) || dict[key] == null) return false;
            try
            {
                value = Convert.ToByte(dict[key]);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadBool(IDictionary dict, string key, out bool value)
        {
            value = false;
            if (!dict.Contains(key) || dict[key] == null) return false;
            try
            {
                value = Convert.ToBoolean(dict[key]);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureRemainingBytes(ObjectReader reader, long required)
        {
            if (RemainingObjectBytes(reader) < required)
            {
                throw new EndOfStreamException($"Camera requires {required} bytes, but only {RemainingObjectBytes(reader)} remain.");
            }
        }

        private static long RemainingObjectBytes(ObjectReader reader)
        {
            var remainingInObject = (long)reader.byteSize - (reader.Position - reader.byteStart);
            var remainingInStream = reader.BaseStream.CanSeek
                ? reader.BaseStream.Length - reader.Position
                : remainingInObject;
            return Math.Min(remainingInObject, remainingInStream);
        }
    }
}
