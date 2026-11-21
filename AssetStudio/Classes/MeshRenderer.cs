using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AssetStudio
{
    public sealed class MeshRenderer : Renderer
    {
        public PPtr<Mesh>? m_AdditionalVertexStreams;
        public PPtr<Mesh>? m_EnlightenVertexStream;

        public MeshRenderer(ObjectReader reader) : base(reader)
        {
            if (version[0] >= 5) //5.0 and up
            {
                m_AdditionalVertexStreams = new PPtr<Mesh>(reader);
            }
            if (version[0] >= 2020) //2020.1 and up
            {
                m_EnlightenVertexStream = new PPtr<Mesh>(reader);
            }
        }
    }
}
