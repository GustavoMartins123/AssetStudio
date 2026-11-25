using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AssetStudio
{
    public class AssetInfo
    {
        public int preloadIndex;
        public int preloadSize;
        public PPtr<Object> asset;

        public AssetInfo(ObjectReader reader)
        {
            preloadIndex = reader.ReadInt32();
            preloadSize = reader.ReadInt32();
            asset = new PPtr<Object>(reader);
        }
    }

    public sealed class AssetBundle : NamedObject
    {
        public PPtr<Object>[] m_PreloadTable;
        public KeyValuePair<string, AssetInfo>[] m_Container;
        public AssetInfo? m_MainAsset;
        public uint m_RuntimeCompatibility;
        public string m_AssetBundleName = string.Empty;
        public string[] m_Dependencies = Array.Empty<string>();
        public bool m_IsStreamedSceneAssetBundle;
        public int m_ExplicitDataLayout;
        public int m_PathFlags;
        public KeyValuePair<string, string>[] m_SceneHashes = Array.Empty<KeyValuePair<string, string>>();

        public AssetBundle(ObjectReader reader) : base(reader)
        {
            var m_PreloadTableSize = reader.ReadInt32();
            reader.CheckArrayLength(m_PreloadTableSize);
            m_PreloadTable = new PPtr<Object>[m_PreloadTableSize];
            for (int i = 0; i < m_PreloadTableSize; i++)
            {
                m_PreloadTable[i] = new PPtr<Object>(reader);
            }

            var m_ContainerSize = reader.ReadInt32();
            reader.CheckArrayLength(m_ContainerSize);
            m_Container = new KeyValuePair<string, AssetInfo>[m_ContainerSize];
            for (int i = 0; i < m_ContainerSize; i++)
            {
                m_Container[i] = new KeyValuePair<string, AssetInfo>(reader.ReadAlignedString(), new AssetInfo(reader));
            }

            var remaining = reader.byteSize - (reader.Position - reader.byteStart);
            var nodes = reader.serializedType?.m_Type?.m_Nodes;
            var hasTypeTree = nodes is { Count: > 0 };
            if (remaining < 16 || (!hasTypeTree && version[0] < 5))
            {
                return;
            }

            try
            {
                bool hasMainAsset = hasTypeTree
                    ? HasTopLevelNode(nodes!, "m_MainAsset")
                    : (version[0] != 5 || version[1] != 4);
                bool hasRuntimeCompatibility = hasTypeTree
                    ? HasTopLevelNode(nodes!, "m_RuntimeCompatibility")
                    : true;
                bool hasAssetBundleName = hasTypeTree
                    ? HasTopLevelNode(nodes!, "m_AssetBundleName")
                    : version[0] >= 5;
                bool hasDependencies = hasTypeTree
                    ? HasTopLevelNode(nodes!, "m_Dependencies")
                    : version[0] >= 5;
                bool hasStreamedFlag = hasTypeTree
                    ? HasTopLevelNode(nodes!, "m_IsStreamedSceneAssetBundle")
                    : version[0] >= 5;
                bool hasExplicitDataLayout = hasTypeTree
                    ? HasTopLevelNode(nodes!, "m_ExplicitDataLayout")
                    : (version[0] > 2017 || (version[0] == 2017 && version[1] >= 3));
                bool hasPathFlags = hasTypeTree
                    ? HasTopLevelNode(nodes!, "m_PathFlags")
                    : version[0] >= 2017;
                bool hasSceneHashes = hasTypeTree
                    ? HasTopLevelNode(nodes!, "m_SceneHashes")
                    : (version[0] > 2017 || (version[0] == 2017 && version[1] >= 3));

                if (hasMainAsset)
                {
                    m_MainAsset = new AssetInfo(reader);
                }

                if (hasRuntimeCompatibility)
                {
                    m_RuntimeCompatibility = reader.ReadUInt32();
                }

                if (hasAssetBundleName)
                {
                    m_AssetBundleName = reader.ReadAlignedString();
                }

                if (hasDependencies)
                {
                    var depCount = reader.ReadInt32();
                    reader.CheckArrayLength(depCount);
                    m_Dependencies = new string[depCount];
                    for (int i = 0; i < depCount; i++)
                    {
                        m_Dependencies[i] = reader.ReadAlignedString();
                    }
                }

                if (hasStreamedFlag)
                {
                    m_IsStreamedSceneAssetBundle = reader.ReadBoolean();
                    reader.AlignStream();
                }

                if (hasExplicitDataLayout)
                {
                    m_ExplicitDataLayout = reader.ReadInt32();
                }

                if (hasPathFlags)
                {
                    m_PathFlags = reader.ReadInt32();
                }

                if (hasSceneHashes)
                {
                    var sceneHashCount = reader.ReadInt32();
                    reader.CheckArrayLength(sceneHashCount);
                    if (sceneHashCount > 0)
                    {
                        var pairs = new KeyValuePair<string, string>[sceneHashCount];
                        for (int i = 0; i < sceneHashCount; i++)
                        {
                            var sceneKey = reader.ReadAlignedString();
                            var cabName = reader.ReadAlignedString();
                            pairs[i] = new KeyValuePair<string, string>(sceneKey, cabName);
                        }
                        m_SceneHashes = pairs;
                    }
                }
            }
            catch
            {
                // Keep m_PreloadTable and m_Container intact if trailing metadata is truncated or custom.
            }
        }

        private static bool HasTopLevelNode(List<TypeTreeNode> nodes, string fieldName)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.m_Level == 1 && string.Equals(node.m_Name, fieldName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
