using System;

namespace AssetStudio
{
    public sealed class VideoPlayer : Behaviour
    {
        public PPtr<VideoClip> m_VideoClip;
        public int m_Target;
        public int m_Source;
        public string m_Url;

        public VideoPlayer(ObjectReader reader) : base(reader)
        {
            try
            {
                m_VideoClip = new PPtr<VideoClip>(reader);
                m_Target = reader.ReadInt32();
                m_Source = reader.ReadInt32();
                m_Url = reader.ReadAlignedString();
            }
            catch
            {
                m_Url ??= string.Empty;
            }
        }

        public override string Dump()
        {
            string name = "VideoPlayer";
            if (m_GameObject.TryGet(out var go) && go != null)
            {
                name = go.m_Name;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"VideoPlayer: {name}");
            sb.AppendLine($"  PathID: {m_PathID}");
            sb.AppendLine($"  m_VideoClip (PPtr): PathID = {m_VideoClip.m_PathID}, FileID = {m_VideoClip.m_FileID}");
            sb.AppendLine($"  m_Target: {m_Target}");
            sb.AppendLine($"  m_Source: {m_Source} ({(m_Source == 1 ? "URL" : "VideoClip")})");
            sb.AppendLine($"  m_Url: \"{m_Url}\"");
            return sb.ToString();
        }
    }
}
