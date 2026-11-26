using System.Collections.Generic;

namespace AssetStudio
{
    public static class MonoBehaviourConverter
    {
        public static TypeTree ConvertToTypeTree(this MonoBehaviour m_MonoBehaviour, AssemblyLoader assemblyLoader)
        {
            var m_Type = new TypeTree();
            m_Type.m_Nodes = new List<TypeTreeNode>();
            var helper = new SerializedTypeHelper(m_MonoBehaviour.version);
            helper.AddMonoBehaviour(m_Type.m_Nodes, 0);
            if (m_MonoBehaviour.m_Script.TryGet(out var m_Script))
            {
                var typeDef = assemblyLoader.GetTypeDefinition(m_Script.m_AssemblyName, string.IsNullOrEmpty(m_Script.m_Namespace) ? m_Script.m_ClassName : $"{m_Script.m_Namespace}.{m_Script.m_ClassName}");
                if (typeDef != null)
                {
                    var typeDefinitionConverter = new TypeDefinitionConverter(typeDef, helper, 1);
                    m_Type.m_Nodes.AddRange(typeDefinitionConverter.ConvertToTypeTreeNodes());
                }
            }
            var version = m_MonoBehaviour.version;
            var referencedTrees = new System.Collections.Concurrent.ConcurrentDictionary<(string, string, string), TypeTree?>();
            m_Type.ReferencedTypeResolver = (className, nsName, asmName) => referencedTrees.GetOrAdd(
                (className, nsName, asmName),
                key => ConvertReferencedTypeToTypeTree(assemblyLoader, version, key.Item1, key.Item2, key.Item3));
            return m_Type;
        }

        // TypeTree of a [SerializeReference] class: a root node followed by the serialized fields, as Unity writes it.
        private static TypeTree? ConvertReferencedTypeToTypeTree(AssemblyLoader assemblyLoader, int[] version, string className, string nsName, string asmName)
        {
            var typeDef = assemblyLoader.GetTypeDefinition(asmName, string.IsNullOrEmpty(nsName) ? className : $"{nsName}.{className}");
            if (typeDef == null)
            {
                return null;
            }
            var tree = new TypeTree();
            tree.m_Nodes.Add(new TypeTreeNode(className, "Base", 0, false));
            tree.m_Nodes.AddRange(new TypeDefinitionConverter(typeDef, new SerializedTypeHelper(version), 1).ConvertToTypeTreeNodes());
            return tree;
        }
    }
}
