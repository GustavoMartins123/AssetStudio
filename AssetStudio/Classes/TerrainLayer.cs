using System;
using System.Collections;
using System.IO;

namespace AssetStudio
{
    public sealed class TerrainLayer : NamedObject
    {
        public PPtr<Texture2D> m_DiffuseTexture;
        public PPtr<Texture2D> m_NormalMapTexture;
        public PPtr<Texture2D> m_MaskMapTexture;
        public Vector2 m_TileSize = new Vector2(15f, 15f);
        public Vector2 m_TileOffset;
        public Color m_Specular = new Color(0f, 0f, 0f, 1f);
        public float m_Metallic;
        public float m_Smoothness;
        public float m_NormalScale = 1f;
        public Vector4 m_DiffuseRemapMin = Vector4.Zero;
        public Vector4 m_DiffuseRemapMax = new Vector4(1f, 1f, 1f, 1f);
        public Vector4 m_MaskMapRemapMin = Vector4.Zero;
        public Vector4 m_MaskMapRemapMax = new Vector4(1f, 1f, 1f, 1f);
        public bool m_SmoothnessFromDiffuseAlpha;
        public bool IsTruncated { get; private set; }

        public TerrainLayer(ObjectReader reader)
            : base(PrepareSafeNamedObjectReader(reader, out var baseTruncated))
        {
            this.reader = reader;
            byteSize = reader.byteSize;
            m_Name ??= string.Empty;
            m_DiffuseTexture = new PPtr<Texture2D>(0, 0, assetsFile);
            m_NormalMapTexture = new PPtr<Texture2D>(0, 0, assetsFile);
            m_MaskMapTexture = new PPtr<Texture2D>(0, 0, assetsFile);

            if (baseTruncated)
            {
                IsTruncated = true;
                Logger.Warning($"TerrainLayer (PathID {m_PathID}): truncated header (byteSize {reader.byteSize}).");
                return;
            }

            if (TryReadFromTypeTree(reader))
            {
                return;
            }

            try
            {
                int pptrBytes = reader.m_Version < SerializedFileFormatVersion.Unknown_14 ? 8 : 12;

                EnsureRemainingBytes(reader, pptrBytes * 3L);
                m_DiffuseTexture = new PPtr<Texture2D>(reader);
                m_NormalMapTexture = new PPtr<Texture2D>(reader);
                m_MaskMapTexture = new PPtr<Texture2D>(reader);

                EnsureRemainingBytes(reader, 16); // m_TileSize (8) + m_TileOffset (8)
                m_TileSize = reader.ReadVector2();
                m_TileOffset = reader.ReadVector2();

                if (RemainingObjectBytes(reader) >= 16)
                {
                    m_Specular = reader.ReadColor4();
                }

                if (RemainingObjectBytes(reader) >= 12)
                {
                    m_Metallic = reader.ReadSingle();
                    m_Smoothness = reader.ReadSingle();
                    m_NormalScale = reader.ReadSingle();
                }
                else if (RemainingObjectBytes(reader) >= 8)
                {
                    m_Metallic = reader.ReadSingle();
                    m_Smoothness = reader.ReadSingle();
                }

                if (RemainingObjectBytes(reader) >= 32)
                {
                    m_DiffuseRemapMin = reader.ReadVector4();
                    m_DiffuseRemapMax = reader.ReadVector4();
                }

                if (RemainingObjectBytes(reader) >= 32)
                {
                    m_MaskMapRemapMin = reader.ReadVector4();
                    m_MaskMapRemapMax = reader.ReadVector4();
                }

                if (RemainingObjectBytes(reader) >= 1)
                {
                    m_SmoothnessFromDiffuseAlpha = reader.ReadBoolean();
                    reader.AlignStream();
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                Logger.Warning($"TerrainLayer (PathID {m_PathID}): failed to read fields ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private static ObjectReader PrepareSafeNamedObjectReader(ObjectReader reader, out bool truncated)
        {
            truncated = false;
            var savedPos = reader.Position;
            try
            {
                reader.Reset();
                long minHeader = reader.platform == BuildTarget.NoTarget ? 8 : 4;
                if (RemainingObjectBytes(reader) < minHeader)
                {
                    truncated = true;
                    return TypeTreeHelper.CreateSyntheticZeroObjectReader(reader, 64);
                }

                if (reader.platform == BuildTarget.NoTarget)
                {
                    reader.Position += 4;
                }

                int strLen = reader.ReadInt32();
                if (strLen < 0 || strLen > RemainingObjectBytes(reader))
                {
                    truncated = true;
                    reader.Position = savedPos;
                    return TypeTreeHelper.CreateSyntheticZeroObjectReader(reader, 64);
                }

                reader.Reset();
                return reader;
            }
            catch
            {
                truncated = true;
                reader.Position = savedPos;
                return TypeTreeHelper.CreateSyntheticZeroObjectReader(reader, 64);
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

                if (dict.Contains("m_Name") && dict["m_Name"] is string nameStr)
                {
                    m_Name = nameStr;
                }

                if (TryReadPPtr(dict, "m_DiffuseTexture", out int diffFileId, out long diffPathId))
                {
                    m_DiffuseTexture = new PPtr<Texture2D>(diffFileId, diffPathId, assetsFile);
                }

                if (TryReadPPtr(dict, "m_NormalMapTexture", out int nrmFileId, out long nrmPathId))
                {
                    m_NormalMapTexture = new PPtr<Texture2D>(nrmFileId, nrmPathId, assetsFile);
                }

                if (TryReadPPtr(dict, "m_MaskMapTexture", out int maskFileId, out long maskPathId))
                {
                    m_MaskMapTexture = new PPtr<Texture2D>(maskFileId, maskPathId, assetsFile);
                }

                if (TryReadVector2(dict, "m_TileSize", out var tileSize))
                {
                    m_TileSize = tileSize;
                }

                if (TryReadVector2(dict, "m_TileOffset", out var tileOffset))
                {
                    m_TileOffset = tileOffset;
                }

                if (TryReadColor(dict, "m_Specular", out var specular))
                {
                    m_Specular = specular;
                }

                if (TryReadFloat(dict, "m_Metallic", out var metallic))
                {
                    m_Metallic = metallic;
                }

                if (TryReadFloat(dict, "m_Smoothness", out var smoothness))
                {
                    m_Smoothness = smoothness;
                }

                if (TryReadFloat(dict, "m_NormalScale", out var normalScale))
                {
                    m_NormalScale = normalScale;
                }

                if (TryReadVector4(dict, "m_DiffuseRemapMin", out var diffMin))
                {
                    m_DiffuseRemapMin = diffMin;
                }

                if (TryReadVector4(dict, "m_DiffuseRemapMax", out var diffMax))
                {
                    m_DiffuseRemapMax = diffMax;
                }

                if (TryReadVector4(dict, "m_MaskMapRemapMin", out var maskMin))
                {
                    m_MaskMapRemapMin = maskMin;
                }

                if (TryReadVector4(dict, "m_MaskMapRemapMax", out var maskMax))
                {
                    m_MaskMapRemapMax = maskMax;
                }

                if (TryReadBool(dict, "m_SmoothnessFromDiffuseAlpha", out var smoothnessFromAlpha))
                {
                    m_SmoothnessFromDiffuseAlpha = smoothnessFromAlpha;
                }

                return true;
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                Logger.Warning($"TerrainLayer (PathID {m_PathID}): TypeTree read failed ({ex.GetType().Name}: {ex.Message}).");
                return true;
            }
        }

        private static bool TryReadPPtr(IDictionary dict, string key, out int fileId, out long pathId)
        {
            fileId = 0;
            pathId = 0;
            if (!dict.Contains(key) || dict[key] is not IDictionary pptrDict)
            {
                return false;
            }

            if (!pptrDict.Contains("m_FileID") || !pptrDict.Contains("m_PathID"))
            {
                return false;
            }

            try
            {
                fileId = Convert.ToInt32(pptrDict["m_FileID"]);
                pathId = Convert.ToInt64(pptrDict["m_PathID"]);
                return true;
            }
            catch
            {
                return false;
            }
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

        private static bool TryReadVector4(IDictionary dict, string key, out Vector4 value)
        {
            value = Vector4.Zero;
            if (!dict.Contains(key) || dict[key] is not IDictionary v)
            {
                return false;
            }

            TryReadFloat(v, "x", out float x);
            TryReadFloat(v, "y", out float y);
            TryReadFloat(v, "z", out float z);
            TryReadFloat(v, "w", out float w);
            value = new Vector4(x, y, z, w);
            return true;
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
                throw new EndOfStreamException($"TerrainLayer requires {required} bytes, but only {RemainingObjectBytes(reader)} remain.");
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
