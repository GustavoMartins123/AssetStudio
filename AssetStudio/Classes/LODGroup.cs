using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;

namespace AssetStudio
{
    public sealed class LOD
    {
        public float screenRelativeHeight;
        public float fadeTransitionWidth;
        public PPtr<Renderer>[] renderers = Array.Empty<PPtr<Renderer>>();
    }

    public sealed class LODGroup : Component
    {
        private const int MaxReasonableLodLevels = 64;
        private const int MaxReasonableRenderersPerLod = 65_536;

        public Vector3 m_LocalReferencePoint;
        public float m_Size;
        public int m_FadeMode;
        public bool m_AnimateCrossFading;
        public bool m_LastLODIsBillboard;
        public LOD[] m_LODs = Array.Empty<LOD>();
        public bool m_Enabled = true;
        public bool IsTruncated { get; private set; }

        public LODGroup(ObjectReader reader) : base(reader)
        {
            if (TryReadFromTypeTree(reader))
            {
                return;
            }

            try
            {
                EnsureRemainingBytes(reader, 16); // Vector3 (12) + float (4)
                m_LocalReferencePoint = reader.ReadVector3();
                m_Size = reader.ReadSingle();

                bool isUnity5OrUp = version != null && version.Length > 0 && version[0] >= 5;
                if (isUnity5OrUp)
                {
                    EnsureRemainingBytes(reader, 8); // m_FadeMode (4) + bool(s) + align (4)
                    m_FadeMode = reader.ReadInt32();
                    m_AnimateCrossFading = reader.ReadBoolean();
                    if (version![0] > 2018 || (version[0] == 2018 && version.Length > 1 && version[1] >= 3))
                    {
                        m_LastLODIsBillboard = reader.ReadBoolean();
                    }
                    reader.AlignStream();
                }

                EnsureRemainingBytes(reader, 4);
                int lodCount = reader.ReadInt32();
                if (lodCount < 0 || lodCount > MaxReasonableLodLevels)
                {
                    throw new OverflowException($"Invalid LODGroup.m_LODs length: {lodCount}.");
                }

                int minLodBytes = isUnity5OrUp ? 12 : 8; // screenRelativeHeight + [fadeTransitionWidth] + renderersCount
                reader.CheckArrayLength(lodCount, minLodBytes);

                int pptrBytes = reader.m_Version < SerializedFileFormatVersion.Unknown_14 ? 8 : 12;
                var lods = new LOD[lodCount];
                for (int i = 0; i < lodCount; i++)
                {
                    EnsureRemainingBytes(reader, minLodBytes);
                    var lod = new LOD
                    {
                        screenRelativeHeight = reader.ReadSingle(),
                        fadeTransitionWidth = isUnity5OrUp ? reader.ReadSingle() : 0f
                    };

                    int rendererCount = reader.ReadInt32();
                    if (rendererCount < 0 || rendererCount > MaxReasonableRenderersPerLod)
                    {
                        throw new OverflowException($"Invalid LOD.renderers length: {rendererCount}.");
                    }

                    reader.CheckArrayLength(rendererCount, pptrBytes);
                    var renderers = rendererCount == 0
                        ? Array.Empty<PPtr<Renderer>>()
                        : new PPtr<Renderer>[rendererCount];
                    for (int r = 0; r < rendererCount; r++)
                    {
                        EnsureRemainingBytes(reader, pptrBytes);
                        renderers[r] = new PPtr<Renderer>(reader);
                    }

                    lod.renderers = renderers;
                    lods[i] = lod;
                }

                m_LODs = lods;

                if (RemainingObjectBytes(reader) >= 1)
                {
                    m_Enabled = reader.ReadBoolean();
                    reader.AlignStream();
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                m_LODs ??= Array.Empty<LOD>();
                Logger.Warning($"LODGroup (PathID {m_PathID}): failed to read LOD levels ({ex.GetType().Name}: {ex.Message}).");
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

                if (TryReadVector3(dict, "m_LocalReferencePoint", out var refPt))
                {
                    m_LocalReferencePoint = refPt;
                }

                if (TryReadFloat(dict, "m_Size", out var size))
                {
                    m_Size = size;
                }

                if (TryReadInt(dict, "m_FadeMode", out var fadeMode))
                {
                    m_FadeMode = fadeMode;
                }

                if (TryReadBool(dict, "m_AnimateCrossFading", out var animateCrossFading))
                {
                    m_AnimateCrossFading = animateCrossFading;
                }

                if (TryReadBool(dict, "m_LastLODIsBillboard", out var lastLodBillboard))
                {
                    m_LastLODIsBillboard = lastLodBillboard;
                }

                if (TryReadBool(dict, "m_Enabled", out var enabled))
                {
                    m_Enabled = enabled;
                }

                if (dict.Contains("m_LODs") && dict["m_LODs"] is IEnumerable lodItems && dict["m_LODs"] is not string)
                {
                    var lods = new List<LOD>();
                    foreach (var lodObj in lodItems)
                    {
                        if (lods.Count >= MaxReasonableLodLevels)
                        {
                            break;
                        }

                        if (lodObj is not IDictionary lodDict)
                        {
                            continue;
                        }

                        var lod = new LOD();
                        if (TryReadFloat(lodDict, "screenRelativeHeight", out var screenHeight))
                        {
                            lod.screenRelativeHeight = screenHeight;
                        }
                        if (TryReadFloat(lodDict, "fadeTransitionWidth", out var fadeWidth))
                        {
                            lod.fadeTransitionWidth = fadeWidth;
                        }

                        var renderers = new List<PPtr<Renderer>>();
                        if (lodDict.Contains("renderers") && lodDict["renderers"] is IEnumerable rendItems && lodDict["renderers"] is not string)
                        {
                            foreach (var rendObj in rendItems)
                            {
                                if (renderers.Count >= MaxReasonableRenderersPerLod)
                                {
                                    break;
                                }

                                if (rendObj is not IDictionary rendEntry)
                                {
                                    continue;
                                }

                                IDictionary? pptrDict = rendEntry.Contains("renderer") && rendEntry["renderer"] is IDictionary inner
                                    ? inner
                                    : rendEntry;

                                if (TryReadPPtr(pptrDict, out int fileId, out long pathId))
                                {
                                    renderers.Add(new PPtr<Renderer>(fileId, pathId, assetsFile));
                                }
                            }
                        }

                        lod.renderers = renderers.ToArray();
                        lods.Add(lod);
                    }

                    m_LODs = lods.ToArray();
                }

                return true;
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                m_LODs ??= Array.Empty<LOD>();
                Logger.Warning($"LODGroup (PathID {m_PathID}): TypeTree read failed ({ex.GetType().Name}: {ex.Message}).");
                return true;
            }
        }

        private static bool TryReadPPtr(IDictionary dict, out int fileId, out long pathId)
        {
            fileId = 0;
            pathId = 0;
            if (!dict.Contains("m_FileID") || !dict.Contains("m_PathID"))
            {
                return false;
            }

            if (!TryConvertInt(dict["m_FileID"], out fileId) || !TryConvertLong(dict["m_PathID"], out pathId))
            {
                return false;
            }

            return true;
        }

        private static bool TryReadVector3(IDictionary dict, string key, out Vector3 value)
        {
            value = Vector3.Zero;
            if (!dict.Contains(key) || dict[key] is not IDictionary vecDict)
            {
                return false;
            }

            TryReadFloat(vecDict, "x", out float x);
            TryReadFloat(vecDict, "y", out float y);
            TryReadFloat(vecDict, "z", out float z);
            value = new Vector3(x, y, z);
            return true;
        }

        private static bool TryReadFloat(IDictionary dict, string key, out float value)
        {
            value = 0f;
            if (!dict.Contains(key) || dict[key] == null)
            {
                return false;
            }

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
            return dict.Contains(key) && TryConvertInt(dict[key], out value);
        }

        private static bool TryReadBool(IDictionary dict, string key, out bool value)
        {
            value = false;
            if (!dict.Contains(key) || dict[key] == null)
            {
                return false;
            }

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

        private static bool TryConvertInt(object? raw, out int value)
        {
            value = 0;
            if (raw == null) return false;
            try
            {
                value = Convert.ToInt32(raw);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryConvertLong(object? raw, out long value)
        {
            value = 0;
            if (raw == null) return false;
            try
            {
                value = Convert.ToInt64(raw);
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
                throw new EndOfStreamException($"LODGroup requires {required} bytes, but only {RemainingObjectBytes(reader)} remain.");
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
