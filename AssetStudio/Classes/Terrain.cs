using System;
using System.Collections;
using System.IO;

namespace AssetStudio
{
    public sealed class Terrain : Behaviour
    {
        public PPtr<TerrainData> m_TerrainData;
        public float m_TreeDistance = 5000f;
        public float m_TreeBillboardDistance = 50f;
        public float m_TreeCrossFadeLength = 5f;
        public int m_TreeMaximumFullLODCount = 50;
        public float m_DetailObjectDistance = 80f;
        public float m_DetailObjectDensity = 1f;
        public float m_HeightmapPixelError = 5f;
        public float m_SplatMapDistance = 1000f;
        public int m_HeightmapMaximumLOD;
        public int m_ShadowCastingMode = 2;
        public bool m_CastShadows = true;
        public bool m_DrawHeightmap = true;
        public bool m_DrawInstanced;
        public bool m_DrawTreesAndFoliage = true;
        public int m_ReflectionProbeUsage = 1;
        public PPtr<Material> m_MaterialTemplate;
        public bool IsTruncated { get; private set; }

        public bool Enabled => m_Enabled != 0;

        public Terrain(ObjectReader reader)
            : base(PrepareSafeBehaviourReader(reader, out var baseTruncated))
        {
            this.reader = reader;
            byteSize = reader.byteSize;
            m_TerrainData = new PPtr<TerrainData>(0, 0, assetsFile);
            m_MaterialTemplate = new PPtr<Material>(0, 0, assetsFile);

            if (baseTruncated)
            {
                IsTruncated = true;
                Logger.Warning($"Terrain (PathID {m_PathID}): truncated Behaviour header (byteSize {reader.byteSize}).");
                return;
            }

            if (TryReadFromTypeTree(reader))
            {
                return;
            }

            try
            {
                int pptrBytes = reader.m_Version < SerializedFileFormatVersion.Unknown_14 ? 8 : 12;
                bool is5_0OrUp = IsVersionAtLeast(5, 0);
                bool is2018_3OrUp = IsVersionAtLeast(2018, 3);
                bool is2019_1OrUp = IsVersionAtLeast(2019, 1);
                bool is2019_2OrUp = IsVersionAtLeast(2019, 2);
                bool is2021_1OrUp = IsVersionAtLeast(2021, 1);

                EnsureRemainingBytes(reader, pptrBytes);
                m_TerrainData = new PPtr<TerrainData>(reader);

                EnsureRemainingBytes(reader, 36);
                m_TreeDistance = reader.ReadSingle();
                m_TreeBillboardDistance = reader.ReadSingle();
                m_TreeCrossFadeLength = reader.ReadSingle();
                m_TreeMaximumFullLODCount = reader.ReadInt32();
                m_DetailObjectDistance = reader.ReadSingle();
                m_DetailObjectDensity = reader.ReadSingle();
                m_HeightmapPixelError = reader.ReadSingle();
                m_SplatMapDistance = reader.ReadSingle();
                m_HeightmapMaximumLOD = reader.ReadInt32();

                if (is2019_1OrUp)
                {
                    if (RemainingObjectBytes(reader) >= 4)
                    {
                        m_ShadowCastingMode = reader.ReadInt32();
                        m_CastShadows = m_ShadowCastingMode != 0;
                    }

                    if (RemainingObjectBytes(reader) >= 1)
                    {
                        m_DrawHeightmap = reader.ReadBoolean();
                    }

                    if (RemainingObjectBytes(reader) >= 1)
                    {
                        m_DrawInstanced = reader.ReadBoolean();
                    }

                    if (RemainingObjectBytes(reader) >= 1)
                    {
                        m_DrawTreesAndFoliage = reader.ReadBoolean();
                    }

                    if (is2021_1OrUp && RemainingObjectBytes(reader) >= 1)
                    {
                        _ = reader.ReadBoolean(); // m_StaticShadowCaster
                    }

                    reader.AlignStream();

                    if (RemainingObjectBytes(reader) >= 4)
                    {
                        m_ReflectionProbeUsage = reader.ReadInt32();
                    }

                    if (!is2019_2OrUp && RemainingObjectBytes(reader) >= 28)
                    {
                        // Unity 2019.1 kept legacy material settings right before m_MaterialTemplate.
                        _ = reader.ReadInt32();  // m_MaterialType
                        _ = reader.ReadColor4(); // m_LegacySpecular
                        _ = reader.ReadSingle(); // m_LegacyShininess
                        _ = reader.ReadBoolean(); // m_UseDefaultSmoothness
                        reader.AlignStream();
                    }

                    if (RemainingObjectBytes(reader) >= pptrBytes)
                    {
                        m_MaterialTemplate = new PPtr<Material>(reader);
                    }
                }
                else
                {
                    if (RemainingObjectBytes(reader) >= 1)
                    {
                        m_CastShadows = reader.ReadBoolean();
                        m_ShadowCastingMode = m_CastShadows ? 1 : 0;
                    }

                    if (RemainingObjectBytes(reader) >= 1)
                    {
                        m_DrawHeightmap = reader.ReadBoolean();
                    }

                    if (is2018_3OrUp && RemainingObjectBytes(reader) >= 1)
                    {
                        m_DrawInstanced = reader.ReadBoolean();
                    }

                    if (RemainingObjectBytes(reader) >= 1)
                    {
                        m_DrawTreesAndFoliage = reader.ReadBoolean();
                    }

                    reader.AlignStream();

                    if (is5_0OrUp)
                    {
                        if (RemainingObjectBytes(reader) >= 4)
                        {
                            m_ReflectionProbeUsage = reader.ReadInt32();
                        }

                        if (RemainingObjectBytes(reader) >= 28)
                        {
                            _ = reader.ReadInt32();  // m_MaterialType
                            _ = reader.ReadColor4(); // m_LegacySpecular
                            _ = reader.ReadSingle(); // m_LegacyShininess
                            _ = reader.ReadBoolean(); // m_UseDefaultSmoothness
                            reader.AlignStream();
                        }

                        if (RemainingObjectBytes(reader) >= pptrBytes)
                        {
                            m_MaterialTemplate = new PPtr<Material>(reader);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                Logger.Warning($"Terrain (PathID {m_PathID}): failed to read fields ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private static ObjectReader PrepareSafeBehaviourReader(ObjectReader reader, out bool truncated)
        {
            truncated = false;
            var savedPos = reader.Position;
            try
            {
                reader.Reset();
                int pptrBytes = reader.m_Version < SerializedFileFormatVersion.Unknown_14 ? 8 : 12;
                long required = (reader.platform == BuildTarget.NoTarget ? 4 : 0) + pptrBytes + 1;
                if (RemainingObjectBytes(reader) < required)
                {
                    truncated = true;
                    return TypeTreeHelper.CreateSyntheticZeroObjectReader(reader, 64);
                }

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

                if (TryReadPPtr(dict, "m_GameObject", out int goFileId, out long goPathId))
                {
                    m_GameObject = new PPtr<GameObject>(goFileId, goPathId, assetsFile);
                }

                if (TryReadByte(dict, "m_Enabled", out var enabledByte))
                {
                    m_Enabled = enabledByte;
                }

                if (TryReadPPtr(dict, "m_TerrainData", out int tdFileId, out long tdPathId))
                {
                    m_TerrainData = new PPtr<TerrainData>(tdFileId, tdPathId, assetsFile);
                }

                if (TryReadFloat(dict, "m_TreeDistance", out var treeDist))
                {
                    m_TreeDistance = treeDist;
                }

                if (TryReadFloat(dict, "m_TreeBillboardDistance", out var billboardDist))
                {
                    m_TreeBillboardDistance = billboardDist;
                }

                if (TryReadFloat(dict, "m_TreeCrossFadeLength", out var crossFade))
                {
                    m_TreeCrossFadeLength = crossFade;
                }

                if (TryReadInt(dict, "m_TreeMaximumFullLODCount", out var maxLodTrees))
                {
                    m_TreeMaximumFullLODCount = maxLodTrees;
                }

                if (TryReadFloat(dict, "m_DetailObjectDistance", out var detailDist))
                {
                    m_DetailObjectDistance = detailDist;
                }

                if (TryReadFloat(dict, "m_DetailObjectDensity", out var detailDensity))
                {
                    m_DetailObjectDensity = detailDensity;
                }

                if (TryReadFloat(dict, "m_HeightmapPixelError", out var pixelError))
                {
                    m_HeightmapPixelError = pixelError;
                }

                if (TryReadFloat(dict, "m_SplatMapDistance", out var splatDist))
                {
                    m_SplatMapDistance = splatDist;
                }

                if (TryReadInt(dict, "m_HeightmapMaximumLOD", out var maxLod))
                {
                    m_HeightmapMaximumLOD = maxLod;
                }

                if (TryReadInt(dict, "m_ShadowCastingMode", out var shadowMode))
                {
                    m_ShadowCastingMode = shadowMode;
                    m_CastShadows = shadowMode != 0;
                }
                else if (TryReadBool(dict, "m_CastShadows", out var castShadows))
                {
                    m_CastShadows = castShadows;
                    m_ShadowCastingMode = castShadows ? 1 : 0;
                }

                if (TryReadBool(dict, "m_DrawHeightmap", out var drawHeightmap))
                {
                    m_DrawHeightmap = drawHeightmap;
                }

                if (TryReadBool(dict, "m_DrawInstanced", out var drawInstanced))
                {
                    m_DrawInstanced = drawInstanced;
                }

                if (TryReadBool(dict, "m_DrawTreesAndFoliage", out var drawTrees))
                {
                    m_DrawTreesAndFoliage = drawTrees;
                }

                if (TryReadInt(dict, "m_ReflectionProbeUsage", out var reflUsage))
                {
                    m_ReflectionProbeUsage = reflUsage;
                }

                if (TryReadPPtr(dict, "m_MaterialTemplate", out int matFileId, out long matPathId))
                {
                    m_MaterialTemplate = new PPtr<Material>(matFileId, matPathId, assetsFile);
                }

                return true;
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                Logger.Warning($"Terrain (PathID {m_PathID}): TypeTree read failed ({ex.GetType().Name}: {ex.Message}).");
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
                throw new EndOfStreamException($"Terrain requires {required} bytes, but only {RemainingObjectBytes(reader)} remain.");
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
