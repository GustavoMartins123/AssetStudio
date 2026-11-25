using System;
using System.Collections;
using System.Collections.Specialized;
using System.IO;

namespace AssetStudio
{
    public sealed class SkinnedMeshRenderer : Renderer
    {
        public PPtr<Mesh> m_Mesh;
        public PPtr<Transform>[] m_Bones;
        public float[] m_BlendShapeWeights = Array.Empty<float>();
        public PPtr<Transform>? m_RootBone;
        public AABB? m_AABB;
        public bool m_DirtyAABB;
        public bool UsedTypeTreeFallback { get; private set; }
        public bool IsTruncated { get; private set; }
        public string? ParseDiagnostic { get; private set; }

        public SkinnedMeshRenderer(ObjectReader reader)
            : base(PrepareBaseReader(reader, out var parsePlan))
        {
            this.reader = reader;
            byteSize = reader.byteSize;
            m_Materials ??= Array.Empty<PPtr<Material>>();
            m_Mesh = new PPtr<Mesh>(0, 0, assetsFile);
            m_Bones = Array.Empty<PPtr<Transform>>();

            if (parsePlan.UseDirectBinary)
            {
                try
                {
                    int m_Quality = reader.ReadInt32();
                    var m_UpdateWhenOffscreen = reader.ReadBoolean();
                    var m_SkinNormals = reader.ReadBoolean(); //3.1.0 and below
                    reader.AlignStream();

                    if (version[0] == 2 && version[1] < 6) //2.6 down
                    {
                        var m_DisableAnimationWhenOffscreen = new PPtr<Animation>(reader);
                    }

                    m_Mesh = new PPtr<Mesh>(reader);

                    var numBones = reader.ReadInt32();
                    reader.CheckArrayLength(numBones);
                    m_Bones = new PPtr<Transform>[numBones];
                    for (int b = 0; b < m_Bones.Length; b++)
                    {
                        m_Bones[b] = new PPtr<Transform>(reader);
                    }

                    if (version[0] > 4 || (version[0] == 4 && version[1] >= 3)) //4.3 and up
                    {
                        m_BlendShapeWeights = reader.ReadSingleArray();
                    }

                    if (version[0] > 3 || (version[0] == 3 && version[1] >= 5)) //3.5 and up
                    {
                        m_RootBone = new PPtr<Transform>(reader);
                    }

                    if (version[0] > 3 || (version[0] == 3 && version[1] >= 4)) //3.4 and up
                    {
                        m_AABB = new AABB(reader);
                        m_DirtyAABB = reader.ReadBoolean();
                        reader.AlignStream();
                    }
                    return;
                }
                catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException or ArgumentOutOfRangeException)
                {
                    parsePlan = RendererBinaryProbeResult.Fallback($"{ex.GetType().Name}: {ex.Message}");
                }
            }

            if (TryPopulateFromTypeTree(reader, parsePlan.Reason))
            {
                return;
            }

            ApplyGracefulDegradation(parsePlan.Reason);
        }

        private static ObjectReader PrepareBaseReader(ObjectReader reader, out RendererBinaryProbeResult parsePlan)
        {
            parsePlan = TypeTreeHelper.ProbeRendererBinaryLayout(reader, isSkinnedMeshRenderer: true);
            return parsePlan.UseDirectBinary
                ? reader
                : TypeTreeHelper.CreateSyntheticZeroObjectReader(reader, 256);
        }

        private bool TryPopulateFromTypeTree(ObjectReader sourceReader, string? binaryFailureReason)
        {
            var typeTree = serializedType?.m_Type;
            if (typeTree?.m_Nodes == null || typeTree.m_Nodes.Count == 0)
            {
                return false;
            }

            if (!TypeTreeHelper.TryReadType(typeTree, sourceReader, out OrderedDictionary? dict, out var typeTreeError) || dict == null)
            {
                ParseDiagnostic = $"SkinnedMeshRenderer (PathID {m_PathID}): binary layout failed ({binaryFailureReason ?? "unknown"}); TypeTree fallback also failed ({typeTreeError ?? "unknown"}).";
                return false;
            }

            TypeTreeHelper.PopulateRendererFromTypeTree(this, dict, sourceReader);

            if (TypeTreeHelper.TryExtractPPtr<Mesh>(dict, "m_Mesh", assetsFile, out var mesh) && mesh != null)
            {
                m_Mesh = mesh;
            }
            else
            {
                m_Mesh = new PPtr<Mesh>(0, 0, assetsFile);
            }

            m_Bones = TypeTreeHelper.TryExtractPPtrArray<Transform>(dict, "m_Bones", assetsFile, out var bones)
                ? bones
                : Array.Empty<PPtr<Transform>>();

            m_BlendShapeWeights = TypeTreeHelper.TryExtractSingleArray(dict, "m_BlendShapeWeights", out var weights)
                ? weights
                : Array.Empty<float>();

            if (TypeTreeHelper.TryExtractPPtr<Transform>(dict, "m_RootBone", assetsFile, out var rootBone))
            {
                m_RootBone = rootBone;
            }

            if (dict.Contains("m_AABB") && dict["m_AABB"] is IDictionary aabbDict)
            {
                TypeTreeHelper.TryExtractVector3(aabbDict, "m_Center", out var center);
                TypeTreeHelper.TryExtractVector3(aabbDict, "m_Extent", out var extent);
                m_AABB = new AABB(TypeTreeHelper.CreateSyntheticZeroObjectReader(sourceReader, 24))
                {
                    m_Center = center,
                    m_Extent = extent
                };
            }

            if (TypeTreeHelper.TryExtractBoolean(dict, "m_DirtyAABB", out var dirtyAabb))
            {
                m_DirtyAABB = dirtyAabb;
            }

            UsedTypeTreeFallback = true;
            IsTruncated = false;
            ParseDiagnostic = string.IsNullOrEmpty(binaryFailureReason)
                ? $"SkinnedMeshRenderer (PathID {m_PathID}): parsed via TypeTree fallback."
                : $"SkinnedMeshRenderer (PathID {m_PathID}): recovered via TypeTree fallback ({binaryFailureReason}).";
            return true;
        }

        private void ApplyGracefulDegradation(string? reason)
        {
            IsTruncated = true;
            UsedTypeTreeFallback = false;
            m_GameObject ??= new PPtr<GameObject>(0, 0, assetsFile);
            m_Materials = Array.Empty<PPtr<Material>>();
            m_Mesh = new PPtr<Mesh>(0, 0, assetsFile);
            m_Bones = Array.Empty<PPtr<Transform>>();
            m_BlendShapeWeights = Array.Empty<float>();
            m_RootBone = null;
            m_AABB = null;
            m_DirtyAABB = false;
            ParseDiagnostic ??= $"SkinnedMeshRenderer (PathID {m_PathID}, byteSize {byteSize}): failed binary deserialization without usable TypeTree ({reason ?? "truncated or incompatible layout"}).";
            Logger.Warning(ParseDiagnostic);
        }
    }
}
