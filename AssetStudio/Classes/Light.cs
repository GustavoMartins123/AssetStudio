using System;
using System.Collections;
using System.IO;

namespace AssetStudio
{
    public enum LightType
    {
        Spot = 0,
        Directional = 1,
        Point = 2,
        Area = 3,
        Disc = 4
    }

    public sealed class ShadowSettings
    {
        public int m_Type;
        public int m_Resolution = -1;
        public int m_CustomResolution = -1;
        public float m_Strength = 1f;
        public float m_Bias = 0.05f;
        public float m_NormalBias = 0.4f;
        public float m_NearPlane = 0.2f;
    }

    public sealed class Light : Behaviour
    {
        public LightType m_Type = LightType.Point;
        public int m_Shape;
        public Color m_Color = new Color(1f, 1f, 1f, 1f);
        public float m_Intensity = 1f;
        public float m_Range = 10f;
        public float m_SpotAngle = 30f;
        public float m_InnerSpotAngle;
        public float m_CookieSize = 10f;
        public ShadowSettings m_Shadows = new ShadowSettings();
        public bool IsTruncated { get; private set; }

        public bool Enabled => m_Enabled != 0;

        public Light(ObjectReader reader) : base(reader)
        {
            if (TryReadFromTypeTree(reader))
            {
                return;
            }

            try
            {
                bool is2019_1OrUp = IsVersionAtLeast(2019, 1);
                bool is5_0OrUp = IsVersionAtLeast(5, 0);
                bool is5_3OrUp = IsVersionAtLeast(5, 3);
                bool is5_4OrUp = IsVersionAtLeast(5, 4);

                EnsureRemainingBytes(reader, 4);
                m_Type = (LightType)reader.ReadInt32();

                if (is2019_1OrUp)
                {
                    EnsureRemainingBytes(reader, 4);
                    m_Shape = reader.ReadInt32();
                }

                EnsureRemainingBytes(reader, 28); // m_Color (16) + m_Intensity (4) + m_Range (4) + m_SpotAngle (4)
                m_Color = reader.ReadColor4();
                m_Intensity = reader.ReadSingle();
                m_Range = reader.ReadSingle();
                m_SpotAngle = reader.ReadSingle();

                if (is2019_1OrUp)
                {
                    EnsureRemainingBytes(reader, 4);
                    m_InnerSpotAngle = reader.ReadSingle();
                }

                if (RemainingObjectBytes(reader) >= 4)
                {
                    m_CookieSize = reader.ReadSingle();
                }

                // ShadowSettings block
                if (RemainingObjectBytes(reader) >= 16)
                {
                    m_Shadows.m_Type = reader.ReadInt32();
                    m_Shadows.m_Resolution = reader.ReadInt32();
                    if (is5_4OrUp && RemainingObjectBytes(reader) >= 4)
                    {
                        m_Shadows.m_CustomResolution = reader.ReadInt32();
                    }
                    if (RemainingObjectBytes(reader) >= 4)
                    {
                        m_Shadows.m_Strength = reader.ReadSingle();
                    }
                    if (RemainingObjectBytes(reader) >= 4)
                    {
                        m_Shadows.m_Bias = reader.ReadSingle();
                    }
                    if (is5_0OrUp && RemainingObjectBytes(reader) >= 4)
                    {
                        m_Shadows.m_NormalBias = reader.ReadSingle();
                    }
                    if (is5_3OrUp && RemainingObjectBytes(reader) >= 4)
                    {
                        m_Shadows.m_NearPlane = reader.ReadSingle();
                    }
                    if (is2019_1OrUp && RemainingObjectBytes(reader) >= 68)
                    {
                        _ = reader.ReadMatrix();  // m_CullingMatrixOverride
                        _ = reader.ReadBoolean(); // m_UseCullingMatrixOverride
                        reader.AlignStream();
                    }
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                m_Shadows ??= new ShadowSettings();
                Logger.Warning($"Light (PathID {m_PathID}): failed to read fields ({ex.GetType().Name}: {ex.Message}).");
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

                if (TryReadInt(dict, "m_Type", out var lightType))
                {
                    m_Type = (LightType)lightType;
                }

                if (TryReadInt(dict, "m_Shape", out var shape))
                {
                    m_Shape = shape;
                }

                if (TryReadColor(dict, "m_Color", out var color))
                {
                    m_Color = color;
                }

                if (TryReadFloat(dict, "m_Intensity", out var intensity))
                {
                    m_Intensity = intensity;
                }

                if (TryReadFloat(dict, "m_Range", out var range))
                {
                    m_Range = range;
                }

                if (TryReadFloat(dict, "m_SpotAngle", out var spotAngle))
                {
                    m_SpotAngle = spotAngle;
                }

                if (TryReadFloat(dict, "m_InnerSpotAngle", out var innerSpotAngle))
                {
                    m_InnerSpotAngle = innerSpotAngle;
                }

                if (TryReadFloat(dict, "m_CookieSize", out var cookieSize))
                {
                    m_CookieSize = cookieSize;
                }

                if (dict.Contains("m_Shadows") && dict["m_Shadows"] is IDictionary shadowsDict)
                {
                    if (TryReadInt(shadowsDict, "m_Type", out var shadowType))
                        m_Shadows.m_Type = shadowType;
                    if (TryReadInt(shadowsDict, "m_Resolution", out var shadowRes))
                        m_Shadows.m_Resolution = shadowRes;
                    if (TryReadInt(shadowsDict, "m_CustomResolution", out var customRes))
                        m_Shadows.m_CustomResolution = customRes;
                    if (TryReadFloat(shadowsDict, "m_Strength", out var strength))
                        m_Shadows.m_Strength = strength;
                    if (TryReadFloat(shadowsDict, "m_Bias", out var bias))
                        m_Shadows.m_Bias = bias;
                    if (TryReadFloat(shadowsDict, "m_NormalBias", out var normalBias))
                        m_Shadows.m_NormalBias = normalBias;
                    if (TryReadFloat(shadowsDict, "m_NearPlane", out var nearPlane))
                        m_Shadows.m_NearPlane = nearPlane;
                }

                return true;
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                m_Shadows ??= new ShadowSettings();
                Logger.Warning($"Light (PathID {m_PathID}): TypeTree read failed ({ex.GetType().Name}: {ex.Message}).");
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

        private static void EnsureRemainingBytes(ObjectReader reader, long required)
        {
            if (RemainingObjectBytes(reader) < required)
            {
                throw new EndOfStreamException($"Light requires {required} bytes, but only {RemainingObjectBytes(reader)} remain.");
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
