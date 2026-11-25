using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AssetStudio
{
    public class StaticBatchInfo
    {
        public ushort firstSubMesh;
        public ushort subMeshCount;

        public StaticBatchInfo(ObjectReader reader)
        {
            firstSubMesh = reader.ReadUInt16();
            subMeshCount = reader.ReadUInt16();
        }
    }

    public abstract class Renderer : Component
    {
        public const byte ShadowCastingModeOff = 0;
        public const byte ShadowCastingModeOn = 1;
        public const byte ShadowCastingModeTwoSided = 2;
        public const byte ShadowCastingModeShadowsOnly = 3;

        public bool m_Enabled = true;
        public byte m_CastShadows = ShadowCastingModeOn;
        public byte m_ReceiveShadows = 1;
        public PPtr<Material>[] m_Materials;
        public StaticBatchInfo? m_StaticBatchInfo;
        public uint[]? m_SubsetIndices;
        public byte m_RayTracingAccelStructBuildFlagsOverride;
        public byte m_RayTracingAccelStructBuildFlags;
        public byte m_SmallMeshCulling;
        public short m_ForceMeshLod;
        public float m_MeshLodSelectionBias;
        public uint m_RenderingLayerMask;
        public int m_RendererPriority;
        public short m_SortingLayer;
        public short m_SortingOrder;
        public int m_MaskInteraction;

        protected Renderer(ObjectReader reader) : base(reader)
        {
            if (version[0] < 5) //5.0 down
            {
                m_Enabled = reader.ReadBoolean();
                m_CastShadows = reader.ReadBoolean() ? ShadowCastingModeOn : ShadowCastingModeOff;
                m_ReceiveShadows = reader.ReadBoolean() ? (byte)1 : (byte)0;
                var m_LightmapIndex = reader.ReadByte();
            }
            else //5.0 and up
            {
                if (version[0] > 5 || (version[0] == 5 && version[1] >= 4)) //5.4 and up
                {
                    m_Enabled = reader.ReadBoolean();
                    m_CastShadows = reader.ReadByte();
                    m_ReceiveShadows = reader.ReadByte();
                    if (version[0] > 2017 || (version[0] == 2017 && version[1] >= 2)) //2017.2 and up
                    {
                        var m_DynamicOccludee = reader.ReadByte();
                    }
                    if (version[0] >= 2021) //2021.1 and up
                    {
                        var m_StaticShadowCaster = reader.ReadByte();
                    }
                    var m_MotionVectors = reader.ReadByte();
                    var m_LightProbeUsage = reader.ReadByte();
                    var m_ReflectionProbeUsage = reader.ReadByte();
                    if (version[0] > 2019 || (version[0] == 2019 && version[1] >= 3)) //2019.3 and up
                    {
                        var m_RayTracingMode = reader.ReadByte();
                    }
                    if (version[0] >= 2020) //2020.1 and up
                    {
                        var m_RayTraceProcedural = reader.ReadByte();
                    }
                    if (version[0] > 2023 || (version[0] == 2023 && version[1] >= 2)) //2023.2 and up
                    {
                        m_RayTracingAccelStructBuildFlagsOverride = reader.ReadByte();
                        m_RayTracingAccelStructBuildFlags = reader.ReadByte();
                    }
                    if (version[0] >= 6000) //6000.0 and up
                    {
                        m_SmallMeshCulling = reader.ReadByte();
                    }
                    reader.AlignStream();
                }
                else
                {
                    m_Enabled = reader.ReadBoolean();
                    reader.AlignStream();
                    m_CastShadows = reader.ReadByte();
                    m_ReceiveShadows = reader.ReadBoolean() ? (byte)1 : (byte)0;
                    reader.AlignStream();
                }

                if (version[0] > 6000 || (version[0] == 6000 && version[1] >= 2)) //6000.2 and up
                {
                    m_ForceMeshLod = reader.ReadInt16();
                    reader.AlignStream();
                    m_MeshLodSelectionBias = reader.ReadSingle();
                }

                if (version[0] >= 2018) //2018 and up
                {
                    m_RenderingLayerMask = reader.ReadUInt32();
                }

                if (version[0] > 2018 || (version[0] == 2018 && version[1] >= 3)) //2018.3 and up
                {
                    m_RendererPriority = reader.ReadInt32();
                }

                var m_LightmapIndex = reader.ReadUInt16();
                var m_LightmapIndexDynamic = reader.ReadUInt16();
            }

            if (version[0] >= 3) //3.0 and up
            {
                var m_LightmapTilingOffset = reader.ReadVector4();
            }

            if (version[0] >= 5) //5.0 and up
            {
                var m_LightmapTilingOffsetDynamic = reader.ReadVector4();
            }

            var m_MaterialsSize = reader.ReadInt32();
            reader.CheckArrayLength(m_MaterialsSize);
            m_Materials = new PPtr<Material>[m_MaterialsSize];
            for (int i = 0; i < m_MaterialsSize; i++)
            {
                m_Materials[i] = new PPtr<Material>(reader);
            }

            if (version[0] < 3) //3.0 down
            {
                var m_LightmapTilingOffset = reader.ReadVector4();
            }
            else //3.0 and up
            {
                if (version[0] > 5 || (version[0] == 5 && version[1] >= 5)) //5.5 and up
                {
                    m_StaticBatchInfo = new StaticBatchInfo(reader);
                }
                else
                {
                    m_SubsetIndices = reader.ReadUInt32Array();
                }

                var m_StaticBatchRoot = new PPtr<Transform>(reader);
            }

            if (version[0] > 5 || (version[0] == 5 && version[1] >= 4)) //5.4 and up
            {
                var m_ProbeAnchor = new PPtr<Transform>(reader);
                var m_LightProbeVolumeOverride = new PPtr<GameObject>(reader);
            }
            else if (version[0] > 3 || (version[0] == 3 && version[1] >= 5)) //3.5 - 5.3
            {
                var m_UseLightProbes = reader.ReadBoolean();
                reader.AlignStream();

                if (version[0] >= 5)//5.0 and up
                {
                    var m_ReflectionProbeUsage = reader.ReadInt32();
                }

                var m_LightProbeAnchor = new PPtr<Transform>(reader); //5.0 and up m_ProbeAnchor
            }

            if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up
            {
                if (version[0] == 4 && version[1] == 3) //4.3
                {
                    var m_SortingLayer = reader.ReadInt16();
                }
                else
                {
                    var m_SortingLayerID = reader.ReadUInt32();
                }

                if (version[0] > 5 || (version[0] == 5 && version[1] >= 6)) //5.6 and up
                {
                    m_SortingLayer = reader.ReadInt16();
                }
                m_SortingOrder = reader.ReadInt16();
                reader.AlignStream();

                if (version[0] > 6000 || (version[0] == 6000 && version[1] >= 3)) //6000.3 and up
                {
                    m_MaskInteraction = reader.ReadInt32();
                }
            }
        }
    }
}
