using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace AssetStudio
{
    public sealed class TerrainSplatPrototype
    {
        public PPtr<Texture2D>? texture;
        public PPtr<Texture2D>? normalMap;
        public Vector2 tileSize = new Vector2(15f, 15f);
        public Vector2 tileOffset;
        public Vector4 specularMetallic;
        public float smoothness;
    }

    public sealed class TerrainSplatDatabase
    {
        public List<TerrainSplatPrototype> m_Splats = new List<TerrainSplatPrototype>();
        public List<PPtr<TerrainLayer>> m_TerrainLayers = new List<PPtr<TerrainLayer>>();
        public List<PPtr<Texture2D>> m_AlphaTextures = new List<PPtr<Texture2D>>();
        public int m_AlphamapResolution;
        public int m_BaseMapResolution;
        public int m_ColorSpace;
        public bool m_MaterialRequiresMetallic;
        public bool m_MaterialRequiresSmoothness;
    }

    public sealed class TerrainHeightmap
    {
        public short[] m_Heights = Array.Empty<short>();
        public byte[] m_Holes = Array.Empty<byte>();
        public int m_Resolution;
        public int m_Width;
        public int m_Height;
        public float m_Thickness = 1f;
        public int m_Levels;
        public Vector3 m_Scale = new Vector3(1f, 1f, 1f);

        public int EffectiveWidth => m_Width > 0 ? m_Width : m_Resolution;
        public int EffectiveHeight => m_Height > 0 ? m_Height : m_Resolution;

        public Vector3 TerrainSize => new Vector3(
            Math.Max(0, EffectiveWidth - 1) * m_Scale.X,
            m_Scale.Y,
            Math.Max(0, EffectiveHeight - 1) * m_Scale.Z);

        public bool HasValidHeightmap =>
            m_Heights != null
            && EffectiveWidth >= 2
            && EffectiveHeight >= 2
            && (long)m_Heights.Length >= (long)EffectiveWidth * EffectiveHeight
            && float.IsFinite(m_Scale.X) && m_Scale.X > 0f
            && float.IsFinite(m_Scale.Y)
            && float.IsFinite(m_Scale.Z) && m_Scale.Z > 0f;
    }

    public sealed class TerrainData : NamedObject
    {
        private const int MaxReasonableHeightsCount = 17_000_000; // Up to 4097x4097
        private const int MaxReasonableHolesCount = 17_000_000;
        private const int MaxReasonableLayers = 1024;
        private const int MaxReasonableAlphaTextures = 256;

        public TerrainSplatDatabase m_SplatDatabase = new TerrainSplatDatabase();
        public TerrainHeightmap m_Heightmap = new TerrainHeightmap();
        public bool IsTruncated { get; private set; }

        public bool HasValidHeightmap => m_Heightmap != null && m_Heightmap.HasValidHeightmap;

        public TerrainData(ObjectReader reader)
            : base(PrepareSafeNamedObjectReader(reader, out var baseTruncated))
        {
            this.reader = reader;
            byteSize = reader.byteSize;
            m_Name ??= string.Empty;

            if (baseTruncated)
            {
                IsTruncated = true;
                Logger.Warning($"TerrainData (PathID {m_PathID}): truncated header (byteSize {reader.byteSize}).");
                return;
            }

            if (TryReadFromTypeTreeStreaming(reader))
            {
                NormalizeHeightmapDimensions();
                return;
            }

            ReadBinaryFallback(reader);
            NormalizeHeightmapDimensions();
        }

        private void NormalizeHeightmapDimensions()
        {
            if (m_Heightmap == null)
            {
                m_Heightmap = new TerrainHeightmap();
                return;
            }

            if (m_Heightmap.m_Resolution > 0)
            {
                if (m_Heightmap.m_Width <= 0) m_Heightmap.m_Width = m_Heightmap.m_Resolution;
                if (m_Heightmap.m_Height <= 0) m_Heightmap.m_Height = m_Heightmap.m_Resolution;
            }
            else if (m_Heightmap.m_Width > 0 && m_Heightmap.m_Height > 0)
            {
                m_Heightmap.m_Resolution = Math.Max(m_Heightmap.m_Width, m_Heightmap.m_Height);
            }
        }

        private void ReadBinaryFallback(ObjectReader reader)
        {
            try
            {
                int pptrBytes = reader.m_Version < SerializedFileFormatVersion.Unknown_14 ? 8 : 12;
                bool is2018_3OrUp = IsVersionAtLeast(2018, 3);
                bool is2019_3OrUp = IsVersionAtLeast(2019, 3);
                bool is5_0OrUp = IsVersionAtLeast(5, 0);
                bool is5_0_1OrUp = IsVersionAtLeast(5, 0, 1);

                // 1. SplatDatabase
                if (is2018_3OrUp)
                {
                    EnsureRemainingBytes(reader, 4);
                    int layerCount = reader.ReadInt32();
                    if (layerCount < 0 || layerCount > MaxReasonableLayers)
                    {
                        throw new OverflowException($"Invalid TerrainData.m_TerrainLayers count: {layerCount}.");
                    }

                    reader.CheckArrayLength(layerCount, pptrBytes);
                    for (int i = 0; i < layerCount; i++)
                    {
                        EnsureRemainingBytes(reader, pptrBytes);
                        m_SplatDatabase.m_TerrainLayers.Add(new PPtr<TerrainLayer>(reader));
                    }
                }
                else
                {
                    EnsureRemainingBytes(reader, 4);
                    int splatCount = reader.ReadInt32();
                    if (splatCount < 0 || splatCount > MaxReasonableLayers)
                    {
                        throw new OverflowException($"Invalid TerrainData.m_Splats count: {splatCount}.");
                    }

                    int minSplatBytes = pptrBytes + 16;
                    reader.CheckArrayLength(splatCount, minSplatBytes);
                    for (int i = 0; i < splatCount; i++)
                    {
                        var proto = new TerrainSplatPrototype();
                        EnsureRemainingBytes(reader, pptrBytes);
                        proto.texture = new PPtr<Texture2D>(reader);
                        if (IsVersionAtLeast(4, 0))
                        {
                            EnsureRemainingBytes(reader, pptrBytes);
                            proto.normalMap = new PPtr<Texture2D>(reader);
                        }
                        EnsureRemainingBytes(reader, 16);
                        proto.tileSize = reader.ReadVector2();
                        proto.tileOffset = reader.ReadVector2();
                        if (is5_0_1OrUp)
                        {
                            EnsureRemainingBytes(reader, 20);
                            proto.specularMetallic = reader.ReadVector4();
                            proto.smoothness = reader.ReadSingle();
                        }
                        else if (is5_0OrUp)
                        {
                            EnsureRemainingBytes(reader, 4);
                            proto.smoothness = reader.ReadSingle();
                        }

                        m_SplatDatabase.m_Splats.Add(proto);
                    }
                }

                EnsureRemainingBytes(reader, 4);
                int alphaCount = reader.ReadInt32();
                if (alphaCount < 0 || alphaCount > MaxReasonableAlphaTextures)
                {
                    throw new OverflowException($"Invalid TerrainData.m_AlphaTextures count: {alphaCount}.");
                }

                reader.CheckArrayLength(alphaCount, pptrBytes);
                for (int i = 0; i < alphaCount; i++)
                {
                    EnsureRemainingBytes(reader, pptrBytes);
                    m_SplatDatabase.m_AlphaTextures.Add(new PPtr<Texture2D>(reader));
                }

                EnsureRemainingBytes(reader, 8);
                m_SplatDatabase.m_AlphamapResolution = reader.ReadInt32();
                m_SplatDatabase.m_BaseMapResolution = reader.ReadInt32();

                // m_ColorSpace, m_MaterialRequiresMetallic, and m_MaterialRequiresSmoothness existed
                // in SplatDatabase on Unity 5.0..2018.2 and were removed in Unity 2018.3 with TerrainLayer.
                if (!is2018_3OrUp && is5_0OrUp && RemainingObjectBytes(reader) >= 4)
                {
                    m_SplatDatabase.m_ColorSpace = reader.ReadInt32();
                    if (is5_0_1OrUp && RemainingObjectBytes(reader) >= 4)
                    {
                        m_SplatDatabase.m_MaterialRequiresMetallic = reader.ReadBoolean();
                        m_SplatDatabase.m_MaterialRequiresSmoothness = reader.ReadBoolean();
                        reader.AlignStream();
                    }
                }

                long afterSplatOffset = reader.Position;

                // 2. DetailDatabase + Heightmap (try sequential read first; if DetailDatabase layout
                //    varies or has detail prototypes without TypeTree, recover Heightmap cleanly from
                //    the mathematically verified binary tail).
                try
                {
                    SkipDetailDatabaseBinary(reader, pptrBytes);
                    ReadHeightmapSequentialBinary(reader, is2019_3OrUp, is5_0OrUp);
                    NormalizeHeightmapDimensions();
                    if (HasValidHeightmap)
                    {
                        return;
                    }
                }
                catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
                {
                    // Fall through to tail-anchored binary recovery below.
                    Logger.Debug($"TerrainData (PathID {m_PathID}): sequential binary read fallback triggered ({ex.GetType().Name}: {ex.Message}).");
                }

                if (TryRecoverHeightmapFromBinaryTail(reader, afterSplatOffset, pptrBytes, is2019_3OrUp, is5_0OrUp))
                {
                    NormalizeHeightmapDimensions();
                    IsTruncated = false;
                    return;
                }

                IsTruncated = true;
                Logger.Warning($"TerrainData (PathID {m_PathID}): unable to recover heightmap from binary payload without TypeTree.");
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                Logger.Warning($"TerrainData (PathID {m_PathID}): failed to read fields ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        private void ReadHeightmapSequentialBinary(ObjectReader reader, bool is2019_3OrUp, bool is5_0OrUp)
        {
            EnsureRemainingBytes(reader, 4);
            int heightCount = reader.ReadInt32();
            if (heightCount < 0 || heightCount > MaxReasonableHeightsCount)
            {
                throw new OverflowException($"Invalid TerrainData.m_Heights count: {heightCount}.");
            }

            EnsureRemainingBytes(reader, (long)heightCount * 2L);
            var heights = heightCount == 0 ? Array.Empty<short>() : new short[heightCount];
            for (int i = 0; i < heightCount; i++)
            {
                heights[i] = reader.ReadInt16();
            }
            reader.AlignStream();
            m_Heightmap.m_Heights = heights;

            if (is2019_3OrUp)
            {
                EnsureRemainingBytes(reader, 4);
                int holesCount = reader.ReadInt32();
                if (holesCount < 0 || holesCount > MaxReasonableHolesCount)
                {
                    throw new OverflowException($"Invalid TerrainData.m_Holes count: {holesCount}.");
                }

                EnsureRemainingBytes(reader, holesCount);
                m_Heightmap.m_Holes = holesCount == 0 ? Array.Empty<byte>() : reader.ReadBytes(holesCount);
                reader.AlignStream();

                EnsureRemainingBytes(reader, 4);
                int holesLodCount = reader.ReadInt32();
                if (holesLodCount < 0 || holesLodCount > MaxReasonableHolesCount)
                {
                    throw new OverflowException($"Invalid TerrainData.m_HolesLOD count: {holesLodCount}.");
                }

                EnsureRemainingBytes(reader, holesLodCount);
                reader.Position += holesLodCount;
                reader.AlignStream();

                EnsureRemainingBytes(reader, 1);
                _ = reader.ReadBoolean(); // m_EnableHolesTextureCompression
                reader.AlignStream();

                // Unity 2019.3–6000.x still serializes m_PrecomputedError + m_MinMaxPatchHeights
                // before m_Resolution / m_Levels / m_Scale.
                long beforeErrorPos = reader.Position;
                bool readErrorVectors = false;
                if (RemainingObjectBytes(reader) >= 28)
                {
                    int preErrCount = reader.ReadInt32();
                    if (preErrCount >= 0 && preErrCount <= MaxReasonableHeightsCount
                        && RemainingObjectBytes(reader) >= (long)preErrCount * 4L + 24)
                    {
                        reader.Position += (long)preErrCount * 4L;
                        int minMaxCount = reader.ReadInt32();
                        if (minMaxCount >= 0 && minMaxCount <= MaxReasonableHeightsCount
                            && RemainingObjectBytes(reader) >= (long)minMaxCount * 4L + 20)
                        {
                            reader.Position += (long)minMaxCount * 4L;
                            int candRes = reader.ReadInt32();
                            int candLevels = reader.ReadInt32();
                            var candScale = reader.ReadVector3();
                            if (IsPlausibleResolution(candRes) && candLevels >= 0 && candLevels <= 14 && IsPlausibleScale(candScale))
                            {
                                m_Heightmap.m_Resolution = candRes;
                                m_Heightmap.m_Levels = candLevels;
                                m_Heightmap.m_Scale = candScale;
                                readErrorVectors = true;
                            }
                        }
                    }
                }

                if (!readErrorVectors)
                {
                    reader.Position = beforeErrorPos;
                    EnsureRemainingBytes(reader, 20); // m_Resolution (4) + m_Levels (4) + m_Scale (12)
                    m_Heightmap.m_Resolution = reader.ReadInt32();
                    m_Heightmap.m_Levels = reader.ReadInt32();
                    m_Heightmap.m_Scale = reader.ReadVector3();
                }
            }
            else
            {
                // Legacy pre-2019.3 Heightmap layout
                EnsureRemainingBytes(reader, 4);
                int precomputedCount = reader.ReadInt32();
                if (precomputedCount < 0 || precomputedCount > MaxReasonableHeightsCount)
                {
                    throw new OverflowException($"Invalid Heightmap.m_PrecomputedError count: {precomputedCount}.");
                }
                EnsureRemainingBytes(reader, (long)precomputedCount * 4L);
                reader.Position += (long)precomputedCount * 4L;

                EnsureRemainingBytes(reader, 4);
                int minMaxCount = reader.ReadInt32();
                if (minMaxCount < 0 || minMaxCount > MaxReasonableHeightsCount)
                {
                    throw new OverflowException($"Invalid Heightmap.m_MinMaxPatchHeights count: {minMaxCount}.");
                }
                EnsureRemainingBytes(reader, (long)minMaxCount * 4L);
                reader.Position += (long)minMaxCount * 4L;

                EnsureRemainingBytes(reader, 8);
                m_Heightmap.m_Width = reader.ReadInt32();
                m_Heightmap.m_Height = reader.ReadInt32();

                if (is5_0OrUp)
                {
                    EnsureRemainingBytes(reader, 4);
                    m_Heightmap.m_Thickness = reader.ReadSingle();
                }

                EnsureRemainingBytes(reader, 16); // m_Levels (4) + m_Scale (12)
                m_Heightmap.m_Levels = reader.ReadInt32();
                m_Heightmap.m_Scale = reader.ReadVector3();
            }
        }

        private bool TryRecoverHeightmapFromBinaryTail(
            ObjectReader reader,
            long minOffset,
            int pptrBytes,
            bool is2019_3OrUp,
            bool is5_0OrUp)
        {
            long objectEnd = reader.byteStart + reader.byteSize;
            if (minOffset < reader.byteStart) minOffset = reader.byteStart;
            if (objectEnd - minOffset < 32)
            {
                return false;
            }

            // 1. Collect candidate heightmap end offsets:
            //    Unity 2018.4+ appends m_PreloadShaders (vector<PPtr<Shader>>) after m_Heightmap.
            var endCandidates = new List<long>(4);
            for (int shaderCount = 0; shaderCount <= 128; shaderCount++)
            {
                long candidateEnd = objectEnd - (4L + (long)shaderCount * pptrBytes);
                if (candidateEnd < minOffset + 24)
                {
                    break;
                }

                reader.Position = candidateEnd;
                if (reader.ReadInt32() != shaderCount)
                {
                    continue;
                }

                bool validShaders = true;
                for (int s = 0; s < shaderCount; s++)
                {
                    int fileId = reader.ReadInt32();
                    long pathId = pptrBytes >= 12 ? reader.ReadInt64() : reader.ReadInt32();
                    if (fileId < 0 || fileId > 1024 || (fileId == 0 && pathId == 0 && shaderCount > 1))
                    {
                        validShaders = false;
                        break;
                    }
                }

                if (validShaders)
                {
                    endCandidates.Add(candidateEnd);
                }
            }

            // Legacy pre-2018.4 (or empty tail): m_Heightmap ends directly at objectEnd.
            if (!endCandidates.Contains(objectEnd))
            {
                endCandidates.Add(objectEnd);
            }

            foreach (var heightmapEnd in endCandidates)
            {
                // Try modern (2019.3+) 20-byte footer first, then legacy 28/24-byte footer.
                if (is2019_3OrUp && TryRecoverWithFooter(reader, minOffset, heightmapEnd, modernLayout: true, hasThickness: false))
                {
                    return true;
                }

                if (!is2019_3OrUp && TryRecoverWithFooter(reader, minOffset, heightmapEnd, modernLayout: false, hasThickness: is5_0OrUp))
                {
                    return true;
                }

                // Cross-layout fallback if Unity version was overridden/ambiguous
                if (TryRecoverWithFooter(reader, minOffset, heightmapEnd, modernLayout: !is2019_3OrUp, hasThickness: is5_0OrUp))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryRecoverWithFooter(
            ObjectReader reader,
            long minOffset,
            long heightmapEnd,
            bool modernLayout,
            bool hasThickness)
        {
            int footerSize = modernLayout ? 20 : (hasThickness ? 28 : 24);
            long footerStart = heightmapEnd - footerSize;
            if (footerStart < minOffset + 16)
            {
                return false;
            }

            reader.Position = footerStart;
            int width;
            int height;
            float thickness = 1f;
            int levels;
            Vector3 scale;

            if (modernLayout)
            {
                int res = reader.ReadInt32();
                levels = reader.ReadInt32();
                scale = reader.ReadVector3();
                width = res;
                height = res;
            }
            else
            {
                width = reader.ReadInt32();
                height = reader.ReadInt32();
                if (hasThickness)
                {
                    thickness = reader.ReadSingle();
                }
                levels = reader.ReadInt32();
                scale = reader.ReadVector3();
            }

            if (!IsPlausibleResolution(width)
                || !IsPlausibleResolution(height)
                || levels < 0 || levels > 14
                || !float.IsFinite(thickness) || thickness < 0f || thickness > 1_000_000f
                || !IsPlausibleScale(scale))
            {
                return false;
            }

            long expectedCountLong = (long)width * height;
            if (expectedCountLong <= 0 || expectedCountLong > MaxReasonableHeightsCount)
            {
                return false;
            }

            int expectedCount = (int)expectedCountLong;
            long heightsAlignedBytes = ((expectedCountLong * 2L) + 3L) & ~3L;
            long maxHeightsPos = footerStart - 4L - heightsAlignedBytes;
            if (maxHeightsPos < minOffset)
            {
                return false;
            }

            // Align scan downwards on 4-byte boundaries from maxHeightsPos down to minOffset.
            long scanStart = maxHeightsPos - ((maxHeightsPos - reader.byteStart) & 3L);
            for (long pos = scanStart; pos >= minOffset; pos -= 4L)
            {
                reader.Position = pos;
                if (reader.ReadInt32() != expectedCount)
                {
                    continue;
                }

                long afterHeights = pos + 4L + heightsAlignedBytes;
                if (!VerifyBetweenHeightsAndFooter(reader, afterHeights, footerStart, modernLayout, out var holes))
                {
                    continue;
                }

                // Exact byte-level match from m_Heights through footerStart! Read m_Heights now.
                reader.Position = pos + 4L;
                var heights = new short[expectedCount];
                for (int i = 0; i < expectedCount; i++)
                {
                    heights[i] = reader.ReadInt16();
                }

                m_Heightmap.m_Heights = heights;
                m_Heightmap.m_Holes = holes;
                m_Heightmap.m_Width = width;
                m_Heightmap.m_Height = height;
                m_Heightmap.m_Resolution = Math.Max(width, height);
                m_Heightmap.m_Thickness = thickness;
                m_Heightmap.m_Levels = levels;
                m_Heightmap.m_Scale = scale;
                return true;
            }

            return false;
        }

        private static bool VerifyBetweenHeightsAndFooter(
            ObjectReader reader,
            long afterHeights,
            long footerStart,
            bool modernLayout,
            out byte[] holes)
        {
            holes = Array.Empty<byte>();
            if (afterHeights > footerStart)
            {
                return false;
            }

            reader.Position = afterHeights;
            if (modernLayout)
            {
                // m_Holes (vector<UInt8>) + align
                if (reader.Position + 4 > footerStart) return false;
                int holesCount = reader.ReadInt32();
                if (holesCount < 0 || holesCount > MaxReasonableHolesCount) return false;
                long holesAligned = ((long)holesCount + 3L) & ~3L;
                if (reader.Position + holesAligned > footerStart) return false;
                byte[] candidateHoles = holesCount == 0 ? Array.Empty<byte>() : reader.ReadBytes(holesCount);
                reader.AlignStream();

                // m_HolesLOD (vector<UInt8>) + align
                if (reader.Position + 4 > footerStart) return false;
                int holesLodCount = reader.ReadInt32();
                if (holesLodCount < 0 || holesLodCount > MaxReasonableHolesCount) return false;
                long holesLodAligned = ((long)holesLodCount + 3L) & ~3L;
                if (reader.Position + holesLodAligned > footerStart) return false;
                reader.Position += holesLodAligned;

                // m_EnableHolesTextureCompression (bool + align -> 4 bytes)
                if (reader.Position + 4 > footerStart) return false;
                byte compFlag = reader.ReadByte();
                if (compFlag > 1) return false;
                reader.AlignStream();

                // Some synthetic or stripped payloads omit m_PrecomputedError/m_MinMaxPatchHeights
                if (reader.Position == footerStart)
                {
                    holes = candidateHoles;
                    return true;
                }

                // Real Unity 2019.3–6000.x: m_PrecomputedError (vector<float>) + m_MinMaxPatchHeights (vector<float>)
                if (!VerifyTwoFloatVectorsExact(reader, footerStart))
                {
                    return false;
                }

                holes = candidateHoles;
                return true;
            }

            return VerifyTwoFloatVectorsExact(reader, footerStart);
        }

        private static bool VerifyTwoFloatVectorsExact(ObjectReader reader, long footerStart)
        {
            if (reader.Position + 4 > footerStart) return false;
            int count1 = reader.ReadInt32();
            if (count1 < 0 || count1 > MaxReasonableHeightsCount) return false;
            long bytes1 = (long)count1 * 4L;
            if (reader.Position + bytes1 + 4 > footerStart) return false;
            reader.Position += bytes1;

            int count2 = reader.ReadInt32();
            if (count2 < 0 || count2 > MaxReasonableHeightsCount) return false;
            long bytes2 = (long)count2 * 4L;
            return reader.Position + bytes2 == footerStart;
        }

        private static bool IsPlausibleResolution(int value)
        {
            if (value < 2 || value > 4097)
            {
                return false;
            }

            // Standard Unity TerrainData resolutions are (2^k + 1): 33, 65, 129, 257, 513, 1025, 2049, 4097.
            // Also allow small synthetic resolutions (2..32) when validated against exact vector boundaries.
            if (value <= 32)
            {
                return true;
            }

            int n = value - 1;
            return (n & (n - 1)) == 0;
        }

        private static bool IsPlausibleScale(Vector3 scale)
        {
            return float.IsFinite(scale.X) && scale.X > 0f && scale.X < 1_000_000f
                && float.IsFinite(scale.Y) && scale.Y >= 0f && scale.Y < 1_000_000f
                && float.IsFinite(scale.Z) && scale.Z > 0f && scale.Z < 1_000_000f;
        }

        private void SkipDetailDatabaseBinary(ObjectReader reader, int pptrBytes)
        {
            // m_Patches
            EnsureRemainingBytes(reader, 4);
            int patchCount = reader.ReadInt32();
            if (patchCount < 0 || patchCount > 1_000_000)
            {
                throw new OverflowException($"Invalid DetailDatabase.m_Patches count: {patchCount}.");
            }

            for (int i = 0; i < patchCount; i++)
            {
                // AABB bounds (24 bytes) + layerIndices + numberOfObjects
                EnsureRemainingBytes(reader, 24);
                reader.Position += 24;

                EnsureRemainingBytes(reader, 4);
                int layerIdxCount = reader.ReadInt32();
                if (layerIdxCount < 0 || layerIdxCount > MaxReasonableHolesCount)
                {
                    throw new OverflowException($"Invalid DetailPatch.layerIndices count: {layerIdxCount}.");
                }
                EnsureRemainingBytes(reader, layerIdxCount);
                reader.Position += layerIdxCount;
                reader.AlignStream();

                EnsureRemainingBytes(reader, 4);
                int numObjCount = reader.ReadInt32();
                if (numObjCount < 0 || numObjCount > MaxReasonableHolesCount)
                {
                    throw new OverflowException($"Invalid DetailPatch.numberOfObjects count: {numObjCount}.");
                }
                EnsureRemainingBytes(reader, numObjCount);
                reader.Position += numObjCount;
                reader.AlignStream();
            }

            // m_DetailPrototypes
            EnsureRemainingBytes(reader, 4);
            int detailProtoCount = reader.ReadInt32();
            if (detailProtoCount < 0 || detailProtoCount > 10_000)
            {
                throw new OverflowException($"Invalid DetailDatabase.m_DetailPrototypes count: {detailProtoCount}.");
            }

            if (detailProtoCount > 0)
            {
                // DetailPrototype layout varies significantly across Unity versions when non-empty without TypeTree.
                throw new InvalidDataException($"DetailDatabase contains {detailProtoCount} detail prototype(s) without TypeTree.");
            }

            // m_PatchCount (4) + m_PatchSamples (4)
            EnsureRemainingBytes(reader, 8);
            _ = reader.ReadInt32();
            _ = reader.ReadInt32();

            // m_RandomRotations (vector<Vector3f>)
            EnsureRemainingBytes(reader, 4);
            int randRotCount = reader.ReadInt32();
            if (randRotCount < 0 || randRotCount > 1_000_000)
            {
                throw new OverflowException($"Invalid DetailDatabase.m_RandomRotations count: {randRotCount}.");
            }
            EnsureRemainingBytes(reader, (long)randRotCount * 12L);
            reader.Position += (long)randRotCount * 12L;

            // WavingGrassTint (16) + Strength (4) + Amount (4) + Speed (4) = 28 bytes
            EnsureRemainingBytes(reader, 28);
            reader.Position += 28;

            // m_TreeInstances
            EnsureRemainingBytes(reader, 4);
            int treeInstCount = reader.ReadInt32();
            if (treeInstCount < 0 || treeInstCount > 5_000_000)
            {
                throw new OverflowException($"Invalid DetailDatabase.m_TreeInstances count: {treeInstCount}.");
            }
            int treeInstSize = IsVersionAtLeast(5, 0) ? 36 : 32;
            EnsureRemainingBytes(reader, (long)treeInstCount * treeInstSize);
            reader.Position += (long)treeInstCount * treeInstSize;

            // m_TreePrototypes
            EnsureRemainingBytes(reader, 4);
            int treeProtoCount = reader.ReadInt32();
            if (treeProtoCount < 0 || treeProtoCount > 100_000)
            {
                throw new OverflowException($"Invalid DetailDatabase.m_TreePrototypes count: {treeProtoCount}.");
            }
            int treeProtoSize = pptrBytes + 4 + (IsVersionAtLeast(2020, 2) ? 4 : 0);
            EnsureRemainingBytes(reader, (long)treeProtoCount * treeProtoSize);
            reader.Position += (long)treeProtoCount * treeProtoSize;

            // m_PreloadTextureAtlasData (vector<PPtr<Texture2D>>)
            EnsureRemainingBytes(reader, 4);
            int atlasCount = reader.ReadInt32();
            if (atlasCount < 0 || atlasCount > 100_000)
            {
                throw new OverflowException($"Invalid DetailDatabase.m_PreloadTextureAtlasData count: {atlasCount}.");
            }
            EnsureRemainingBytes(reader, (long)atlasCount * pptrBytes);
            reader.Position += (long)atlasCount * pptrBytes;

            // Unity 2022.2+: m_DetailScatterMode (int32)
            if (IsVersionAtLeast(2022, 2))
            {
                EnsureRemainingBytes(reader, 4);
                _ = reader.ReadInt32();
            }
        }

        private bool TryReadFromTypeTreeStreaming(ObjectReader reader)
        {
            var typeTree = serializedType?.m_Type;
            if (typeTree?.m_Nodes == null || typeTree.m_Nodes.Count == 0)
            {
                return false;
            }

            var savedPosition = reader.Position;
            try
            {
                var nodes = typeTree.m_Nodes;
                var nextSibling = TypeTreeHelper.GetNextSiblingTable(nodes);
                reader.Reset();

                bool foundHeightmapOrSplat = false;
                for (int i = 1; i < nodes.Count; i++)
                {
                    switch (nodes[i].m_Name)
                    {
                        case "m_Name" when nodes[i].m_Type == "string":
                            if (TypeTreeHelper.TryReadString(nodes, nextSibling, reader, ref i, out var nameStr, depth: 0))
                            {
                                m_Name = nameStr;
                            }
                            break;
                        case "m_SplatDatabase":
                            ReadSplatDatabaseStreaming(nodes, nextSibling, reader, ref i);
                            foundHeightmapOrSplat = true;
                            break;
                        case "m_Heightmap":
                            ReadHeightmapStreaming(nodes, nextSibling, reader, ref i);
                            foundHeightmapOrSplat = true;
                            break;
                        default:
                            // Skips m_DetailDatabase, m_PreloadShaders, etc. in O(1) without boxing!
                            TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref i, depth: 0);
                            break;
                    }
                }

                return foundHeightmapOrSplat;
            }
            catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
            {
                IsTruncated = true;
                Logger.Warning($"TerrainData (PathID {m_PathID}): TypeTree read failed ({ex.GetType().Name}: {ex.Message}).");
                return true;
            }
            catch
            {
                reader.Position = savedPosition;
                return false;
            }
        }

        private void ReadSplatDatabaseStreaming(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index)
        {
            if (!TypeTreeHelper.IsClassNode(nodes, index))
            {
                TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref index, depth: 1);
                return;
            }

            bool align = (nodes[index].m_MetaFlag & 0x4000) != 0;
            int childStart = index + 1;
            int childEnd = nextSibling[index];
            index = childEnd - 1;

            for (int j = childStart; j < childEnd; j++)
            {
                switch (nodes[j].m_Name)
                {
                    case "m_TerrainLayers":
                        ReadPPtrVectorStreaming<TerrainLayer>(nodes, nextSibling, reader, ref j, m_SplatDatabase.m_TerrainLayers, MaxReasonableLayers, depth: 2);
                        break;
                    case "m_AlphaTextures":
                        ReadPPtrVectorStreaming<Texture2D>(nodes, nextSibling, reader, ref j, m_SplatDatabase.m_AlphaTextures, MaxReasonableAlphaTextures, depth: 2);
                        break;
                    case "m_Splats":
                        ReadSplatPrototypeVectorStreaming(nodes, nextSibling, reader, ref j);
                        break;
                    case "m_AlphamapResolution":
                        if (TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref j, out int alphaRes, depth: 2))
                            m_SplatDatabase.m_AlphamapResolution = alphaRes;
                        break;
                    case "m_BaseMapResolution":
                        if (TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref j, out int baseRes, depth: 2))
                            m_SplatDatabase.m_BaseMapResolution = baseRes;
                        break;
                    case "m_ColorSpace":
                        if (TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref j, out int colorSpace, depth: 2))
                            m_SplatDatabase.m_ColorSpace = colorSpace;
                        break;
                    case "m_MaterialRequiresMetallic" when nodes[j].m_Type == "bool":
                        EnsureRemainingBytes(reader, 1);
                        m_SplatDatabase.m_MaterialRequiresMetallic = reader.ReadBoolean();
                        if ((nodes[j].m_MetaFlag & 0x4000) != 0) reader.AlignStream();
                        j = nextSibling[j] - 1;
                        break;
                    case "m_MaterialRequiresSmoothness" when nodes[j].m_Type == "bool":
                        EnsureRemainingBytes(reader, 1);
                        m_SplatDatabase.m_MaterialRequiresSmoothness = reader.ReadBoolean();
                        if ((nodes[j].m_MetaFlag & 0x4000) != 0) reader.AlignStream();
                        j = nextSibling[j] - 1;
                        break;
                    default:
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref j, depth: 2);
                        break;
                }
            }

            if (align) reader.AlignStream();
        }

        private void ReadSplatPrototypeVectorStreaming(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index)
        {
            if (!TryBeginVector(nodes, nextSibling, reader, ref index, MaxReasonableLayers, out int count, out int elemIndex, out bool align))
            {
                return;
            }

            for (int k = 0; k < count; k++)
            {
                int eIdx = elemIndex;
                var proto = ReadSplatPrototypeStreaming(nodes, nextSibling, reader, ref eIdx);
                m_SplatDatabase.m_Splats.Add(proto);
            }

            if (align) reader.AlignStream();
        }

        private TerrainSplatPrototype ReadSplatPrototypeStreaming(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index)
        {
            var proto = new TerrainSplatPrototype();
            if (!TypeTreeHelper.IsClassNode(nodes, index))
            {
                TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref index, depth: 3);
                return proto;
            }

            bool align = (nodes[index].m_MetaFlag & 0x4000) != 0;
            int childStart = index + 1;
            int childEnd = nextSibling[index];
            index = childEnd - 1;

            for (int j = childStart; j < childEnd; j++)
            {
                switch (nodes[j].m_Name)
                {
                    case "texture":
                        proto.texture = ReadPPtrStreaming<Texture2D>(nodes, nextSibling, reader, ref j, depth: 4);
                        break;
                    case "normalMap":
                        proto.normalMap = ReadPPtrStreaming<Texture2D>(nodes, nextSibling, reader, ref j, depth: 4);
                        break;
                    case "tileSize":
                        proto.tileSize = ReadVector2Streaming(nodes, nextSibling, reader, ref j, depth: 4);
                        break;
                    case "tileOffset":
                        proto.tileOffset = ReadVector2Streaming(nodes, nextSibling, reader, ref j, depth: 4);
                        break;
                    case "specularMetallic":
                        proto.specularMetallic = ReadVector4Streaming(nodes, nextSibling, reader, ref j, depth: 4);
                        break;
                    case "smoothness":
                        if (TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out float sm, depth: 4))
                            proto.smoothness = sm;
                        break;
                    default:
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref j, depth: 4);
                        break;
                }
            }

            if (align) reader.AlignStream();
            return proto;
        }

        private void ReadHeightmapStreaming(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index)
        {
            if (!TypeTreeHelper.IsClassNode(nodes, index))
            {
                TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref index, depth: 1);
                return;
            }

            bool align = (nodes[index].m_MetaFlag & 0x4000) != 0;
            int childStart = index + 1;
            int childEnd = nextSibling[index];
            index = childEnd - 1;

            for (int j = childStart; j < childEnd; j++)
            {
                switch (nodes[j].m_Name)
                {
                    case "m_Heights":
                        m_Heightmap.m_Heights = ReadInt16VectorStreaming(nodes, nextSibling, reader, ref j);
                        break;
                    case "m_Holes":
                        m_Heightmap.m_Holes = ReadByteVectorStreaming(nodes, nextSibling, reader, ref j);
                        break;
                    case "m_Resolution":
                        if (TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref j, out int res, depth: 2))
                            m_Heightmap.m_Resolution = res;
                        break;
                    case "m_Width":
                        if (TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref j, out int w, depth: 2))
                            m_Heightmap.m_Width = w;
                        break;
                    case "m_Height":
                        if (TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref j, out int h, depth: 2))
                            m_Heightmap.m_Height = h;
                        break;
                    case "m_Thickness":
                        if (TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out float thickness, depth: 2))
                            m_Heightmap.m_Thickness = thickness;
                        break;
                    case "m_Levels":
                        if (TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref j, out int levels, depth: 2))
                            m_Heightmap.m_Levels = levels;
                        break;
                    case "m_Scale":
                        m_Heightmap.m_Scale = ReadVector3Streaming(nodes, nextSibling, reader, ref j, depth: 2);
                        break;
                    default:
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref j, depth: 2);
                        break;
                }
            }

            if (align) reader.AlignStream();
        }

        private static bool TryBeginVector(
            List<TypeTreeNode> nodes,
            int[] nextSibling,
            ObjectReader reader,
            ref int index,
            int maxCount,
            out int count,
            out int elemIndex,
            out bool align)
        {
            int vecEnd = nextSibling[index];
            align = (nodes[index].m_MetaFlag & 0x4000) != 0;

            int arrayIdx = index + 1;
            if (arrayIdx >= vecEnd || nodes[arrayIdx].m_Type != "Array")
            {
                TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref index, depth: 2);
                count = 0;
                elemIndex = -1;
                return false;
            }

            if ((nodes[arrayIdx].m_MetaFlag & 0x4000) != 0)
            {
                align = true;
            }

            // children of Array: [size, data]
            int sizeIdx = arrayIdx + 1;
            elemIndex = sizeIdx < vecEnd ? nextSibling[sizeIdx] : vecEnd;
            index = vecEnd - 1;

            EnsureRemainingBytes(reader, 4);
            count = reader.ReadInt32();
            if (count < 0 || count > maxCount)
            {
                throw new OverflowException($"Invalid TerrainData vector length: {count}.");
            }

            if (count > 0 && elemIndex >= vecEnd)
            {
                throw new InvalidDataException("Invalid TerrainData vector TypeTree layout.");
            }

            return true;
        }

        private static short[] ReadInt16VectorStreaming(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index)
        {
            if (!TryBeginVector(nodes, nextSibling, reader, ref index, MaxReasonableHeightsCount, out int count, out int elemIndex, out bool align))
            {
                return Array.Empty<short>();
            }

            var elemType = elemIndex < nodes.Count ? nodes[elemIndex].m_Type : "SInt16";
            bool is2Byte = elemType is "SInt16" or "short" or "UInt16" or "unsigned short";
            bool elemAlign = elemIndex < nodes.Count && (nodes[elemIndex].m_MetaFlag & 0x4000) != 0;

            var result = count == 0 ? Array.Empty<short>() : new short[count];
            if (is2Byte && !elemAlign)
            {
                EnsureRemainingBytes(reader, (long)count * 2L);
                for (int i = 0; i < count; i++)
                {
                    result[i] = reader.ReadInt16();
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    int tmp = elemIndex;
                    if (TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref tmp, out int v, depth: 3))
                    {
                        result[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
                    }
                    else
                    {
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref tmp, depth: 3);
                    }
                }
            }

            if (align) reader.AlignStream();
            return result;
        }

        private static byte[] ReadByteVectorStreaming(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index)
        {
            if (!TryBeginVector(nodes, nextSibling, reader, ref index, MaxReasonableHolesCount, out int count, out int elemIndex, out bool align))
            {
                return Array.Empty<byte>();
            }

            var elemType = elemIndex < nodes.Count ? nodes[elemIndex].m_Type : "UInt8";
            bool is1Byte = elemType is "UInt8" or "SInt8" or "char" or "bool";
            bool elemAlign = elemIndex < nodes.Count && (nodes[elemIndex].m_MetaFlag & 0x4000) != 0;

            byte[] result;
            if (is1Byte && !elemAlign)
            {
                EnsureRemainingBytes(reader, count);
                result = count == 0 ? Array.Empty<byte>() : reader.ReadBytes(count);
            }
            else
            {
                result = count == 0 ? Array.Empty<byte>() : new byte[count];
                for (int i = 0; i < count; i++)
                {
                    int tmp = elemIndex;
                    if (TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref tmp, out int v, depth: 3))
                    {
                        result[i] = (byte)Math.Clamp(v, 0, 255);
                    }
                    else
                    {
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref tmp, depth: 3);
                    }
                }
            }

            if (align) reader.AlignStream();
            return result;
        }

        private void ReadPPtrVectorStreaming<T>(
            List<TypeTreeNode> nodes,
            int[] nextSibling,
            ObjectReader reader,
            ref int index,
            List<PPtr<T>> destination,
            int maxCount,
            int depth) where T : Object
        {
            if (!TryBeginVector(nodes, nextSibling, reader, ref index, maxCount, out int count, out int elemIndex, out bool align))
            {
                return;
            }

            for (int k = 0; k < count; k++)
            {
                int eIdx = elemIndex;
                destination.Add(ReadPPtrStreaming<T>(nodes, nextSibling, reader, ref eIdx, depth + 1));
            }

            if (align) reader.AlignStream();
        }

        private PPtr<T> ReadPPtrStreaming<T>(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index, int depth) where T : Object
        {
            if (!TypeTreeHelper.IsClassNode(nodes, index))
            {
                TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref index, depth + 1);
                return new PPtr<T>(0, 0, assetsFile);
            }

            bool align = (nodes[index].m_MetaFlag & 0x4000) != 0;
            int childStart = index + 1;
            int childEnd = nextSibling[index];
            index = childEnd - 1;

            int fileId = 0;
            long pathId = 0;
            for (int j = childStart; j < childEnd; j++)
            {
                switch (nodes[j].m_Name)
                {
                    case "m_FileID":
                        if (!TypeTreeHelper.TryReadInt32(nodes, nextSibling, reader, ref j, out fileId, depth + 1))
                            throw new InvalidDataException("Invalid TerrainData PPtr.m_FileID node.");
                        break;
                    case "m_PathID":
                        if (!TypeTreeHelper.TryReadInt64(nodes, nextSibling, reader, ref j, out pathId, depth + 1))
                            throw new InvalidDataException("Invalid TerrainData PPtr.m_PathID node.");
                        break;
                    default:
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref j, depth + 1);
                        break;
                }
            }

            if (align) reader.AlignStream();
            return new PPtr<T>(fileId, pathId, assetsFile);
        }

        private static Vector2 ReadVector2Streaming(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index, int depth)
        {
            if (!TypeTreeHelper.IsClassNode(nodes, index))
            {
                TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref index, depth + 1);
                return Vector2.Zero;
            }

            bool align = (nodes[index].m_MetaFlag & 0x4000) != 0;
            int childStart = index + 1;
            int childEnd = nextSibling[index];
            index = childEnd - 1;

            float x = 0f, y = 0f;
            for (int j = childStart; j < childEnd; j++)
            {
                switch (nodes[j].m_Name)
                {
                    case "x":
                        TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out x, depth + 1);
                        break;
                    case "y":
                        TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out y, depth + 1);
                        break;
                    default:
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref j, depth + 1);
                        break;
                }
            }

            if (align) reader.AlignStream();
            return new Vector2(x, y);
        }

        private static Vector3 ReadVector3Streaming(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index, int depth)
        {
            if (!TypeTreeHelper.IsClassNode(nodes, index))
            {
                TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref index, depth + 1);
                return Vector3.Zero;
            }

            bool align = (nodes[index].m_MetaFlag & 0x4000) != 0;
            int childStart = index + 1;
            int childEnd = nextSibling[index];
            index = childEnd - 1;

            float x = 0f, y = 0f, z = 0f;
            for (int j = childStart; j < childEnd; j++)
            {
                switch (nodes[j].m_Name)
                {
                    case "x":
                        TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out x, depth + 1);
                        break;
                    case "y":
                        TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out y, depth + 1);
                        break;
                    case "z":
                        TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out z, depth + 1);
                        break;
                    default:
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref j, depth + 1);
                        break;
                }
            }

            if (align) reader.AlignStream();
            return new Vector3(x, y, z);
        }

        private static Vector4 ReadVector4Streaming(List<TypeTreeNode> nodes, int[] nextSibling, ObjectReader reader, ref int index, int depth)
        {
            if (!TypeTreeHelper.IsClassNode(nodes, index))
            {
                TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref index, depth + 1);
                return Vector4.Zero;
            }

            bool align = (nodes[index].m_MetaFlag & 0x4000) != 0;
            int childStart = index + 1;
            int childEnd = nextSibling[index];
            index = childEnd - 1;

            float x = 0f, y = 0f, z = 0f, w = 0f;
            for (int j = childStart; j < childEnd; j++)
            {
                switch (nodes[j].m_Name)
                {
                    case "x":
                        TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out x, depth + 1);
                        break;
                    case "y":
                        TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out y, depth + 1);
                        break;
                    case "z":
                        TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out z, depth + 1);
                        break;
                    case "w":
                        TypeTreeHelper.TryReadSingle(nodes, nextSibling, reader, ref j, out w, depth + 1);
                        break;
                    default:
                        TypeTreeHelper.SkipNode(nodes, nextSibling, reader, ref j, depth + 1);
                        break;
                }
            }

            if (align) reader.AlignStream();
            return new Vector4(x, y, z, w);
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

        private bool IsVersionAtLeast(int major, int minor, int patch = 0)
        {
            if (version == null || version.Length == 0)
            {
                return false;
            }

            if (version[0] != major)
            {
                return version[0] > major;
            }

            int vMinor = version.Length > 1 ? version[1] : 0;
            if (vMinor != minor)
            {
                return vMinor > minor;
            }

            int vPatch = version.Length > 2 ? version[2] : 0;
            return vPatch >= patch;
        }

        private static void EnsureRemainingBytes(ObjectReader reader, long required)
        {
            if (RemainingObjectBytes(reader) < required)
            {
                throw new EndOfStreamException($"TerrainData requires {required} bytes, but only {RemainingObjectBytes(reader)} remain.");
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
