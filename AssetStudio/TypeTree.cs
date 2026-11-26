using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AssetStudio
{
    public class TypeTree
    {
        public List<TypeTreeNode> m_Nodes = new List<TypeTreeNode>();
        public byte[] m_StringBuffer = Array.Empty<byte>();
        /// <summary>
        /// Supplies the TypeTree of a [SerializeReference] class (class, namespace, assembly) when the file does not carry it,
        /// e.g. a MonoBehaviour TypeTree generated from the game assemblies for a build without TypeTrees.
        /// </summary>
        public Func<string, string, string, TypeTree?>? ReferencedTypeResolver;
    }
}
