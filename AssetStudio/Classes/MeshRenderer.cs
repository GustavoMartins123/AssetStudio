using System;
using System.Collections.Specialized;
using System.IO;

namespace AssetStudio
{
    public sealed class MeshRenderer : Renderer
    {
        public PPtr<Mesh>? m_AdditionalVertexStreams;
        public PPtr<Mesh>? m_EnlightenVertexStream;
        public bool UsedTypeTreeFallback { get; private set; }
        public bool IsTruncated { get; private set; }
        public string? ParseDiagnostic { get; private set; }

        public MeshRenderer(ObjectReader reader)
            : base(PrepareBaseReader(reader, out var parsePlan))
        {
            this.reader = reader;
            byteSize = reader.byteSize;
            m_Materials ??= Array.Empty<PPtr<Material>>();

            if (parsePlan.UseDirectBinary)
            {
                try
                {
                    if (version[0] >= 5) //5.0 and up
                    {
                        m_AdditionalVertexStreams = new PPtr<Mesh>(reader);
                    }
                    if (version[0] >= 2020) //2020.1 and up
                    {
                        m_EnlightenVertexStream = new PPtr<Mesh>(reader);
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
            parsePlan = TypeTreeHelper.ProbeRendererBinaryLayout(reader, isSkinnedMeshRenderer: false);
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
                ParseDiagnostic = $"MeshRenderer (PathID {m_PathID}): binary layout failed ({binaryFailureReason ?? "unknown"}); TypeTree fallback also failed ({typeTreeError ?? "unknown"}).";
                return false;
            }

            TypeTreeHelper.PopulateRendererFromTypeTree(this, dict, sourceReader);

            if (TypeTreeHelper.TryExtractPPtr<Mesh>(dict, "m_AdditionalVertexStreams", assetsFile, out var additionalStreams))
            {
                m_AdditionalVertexStreams = additionalStreams;
            }

            if (TypeTreeHelper.TryExtractPPtr<Mesh>(dict, "m_EnlightenVertexStream", assetsFile, out var enlightenStream))
            {
                m_EnlightenVertexStream = enlightenStream;
            }

            UsedTypeTreeFallback = true;
            IsTruncated = false;
            ParseDiagnostic = string.IsNullOrEmpty(binaryFailureReason)
                ? $"MeshRenderer (PathID {m_PathID}): parsed via TypeTree fallback."
                : $"MeshRenderer (PathID {m_PathID}): recovered via TypeTree fallback ({binaryFailureReason}).";
            return true;
        }

        private void ApplyGracefulDegradation(string? reason)
        {
            IsTruncated = true;
            UsedTypeTreeFallback = false;
            m_GameObject ??= new PPtr<GameObject>(0, 0, assetsFile);
            m_Materials = Array.Empty<PPtr<Material>>();
            m_AdditionalVertexStreams = null;
            m_EnlightenVertexStream = null;
            ParseDiagnostic ??= $"MeshRenderer (PathID {m_PathID}, byteSize {byteSize}): failed binary deserialization without usable TypeTree ({reason ?? "truncated or incompatible layout"}).";
            Logger.Warning(ParseDiagnostic);
        }
    }
}
